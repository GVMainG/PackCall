using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Diagnostics;
using PackCall.Core.Entities;
using PackCall.Core.Journaling;
using PackCall.Core.Repositorys;
using PackCall.Core.Services;
using Npgsql;

namespace PackCall.Core.Test;

public partial class CampaignPausePostgresTests
{
    [PostgresFact]
    public async Task MeasurementRecoveryCommitSurvivesJournalFailure()
    {
        using var measurements = new MeasurementCollector(new() { Enabled = true, SessionId = Guid.NewGuid(), OutputDirectory = "unused" });
        await CreateStartedCampaignAsync();
        await using var scope = _provider!.CreateAsyncScope();
        var normal = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var delivery = Assert.Single(await normal.CaptureBatchAsync(1, "old-worker", TimeSpan.FromSeconds(90), default));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var expire = new NpgsqlCommand("UPDATE deliveries SET lock_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id", connection);
        expire.Parameters.AddWithValue("id", delivery.Id);
        await expire.ExecuteNonQueryAsync();
        var service = new DeliveryProcessingService(scope.ServiceProvider.GetRequiredService<IDeliveryRepository>(),
            scope.ServiceProvider.GetRequiredService<ICampaingRepository>(), _sender, new FailingRecoveryJournal(), new(false));
        await Assert.ThrowsAsync<IOException>(() => service.ReleaseExpiredLocksAsync(default));
        Assert.Equal(DeliveryStatus.Waiting, (await ReadDelivery(delivery.Id)).DeliveryStatus);
        var window = measurements.Close()!;
        Assert.Equal(1, window.Counters[new((int)Event.RecoveryRows, Outcome.Success, Purpose.None)].Delta);
        Assert.Equal(1, window.Counters[new((int)Event.JournalFinished, Outcome.Error, Purpose.Recovery)].Delta);
    }

    private sealed class FailingRecoveryJournal : IDeliveryStatusJournal
    {
        public Task AppendAsync(StatusChangeEntry entry, CancellationToken ct) => throw new IOException("injected_recovery_journal_failure");
    }

    [PostgresTheory]
    [InlineData("accepted")] [InlineData("rejected")] [InlineData("journal_error")]
    [InlineData("unknown_commit")] [InlineData("stale")] [InlineData("timeout")]
    public async Task MeasurementMatchesRealPostgresAndHttpIncludingUnknownCommit(string scenario)
    {
        using var collector = new MeasurementCollector(new() { Enabled = true, SessionId = Guid.NewGuid(), OutputDirectory = "unused" });
        await CreateStartedCampaignAsync();
        await using var server = await OperatorServer.Start(async context =>
        {
            if (scenario == "timeout") { await Task.Delay(10000, context.RequestAborted); return; }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(scenario == "rejected"
                ? "{\"result\":\"rejected\",\"code\":\"SimulatedOperatorRejected\"}"
                : "{\"result\":\"accepted\",\"code\":\"AcceptedByProvider\"}");
        });
        using var sender = server.Sender(scenario == "timeout" ? .1 : 5);
        await using var scope = _provider!.CreateAsyncScope();
        var repository = DispatchProxy.Create<IDeliveryRepository, CommitObservationProxy>();
        var proxy = (CommitObservationProxy)repository;
        proxy.Target = scope.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        proxy.Scenario = scenario;
        IDeliveryStatusJournal journal = scope.ServiceProvider.GetRequiredService<IDeliveryStatusJournal>();
        if (scenario == "journal_error") journal = new FailingTerminalJournal(journal);
        var service = new DeliveryProcessingService(repository, scope.ServiceProvider.GetRequiredService<ICampaingRepository>(), sender, journal, new(false));
        var delivery = Assert.Single(await service.CaptureBatchAsync(1, "performance-fixture", TimeSpan.FromSeconds(90), default));
        var exception = await Record.ExceptionAsync(() => service.ProcessAsync(delivery, default));
        var window = collector.Close()!;
        long Count(Event metric, Outcome outcome) => window.Counters.GetValueOrDefault(new((int)metric, outcome, Purpose.None))?.Delta ?? 0;
        var committed = scenario is "accepted" or "rejected" or "journal_error" or "unknown_commit";
        var confirmed = committed && scenario != "unknown_commit";
        var stored = await ReadDelivery(delivery.Id);
        Assert.Equal(committed ? scenario == "rejected" ? DeliveryStatus.Failed : DeliveryStatus.Sent : DeliveryStatus.InProgress, stored.DeliveryStatus);
        Assert.Equal(confirmed ? 1 : 0, Count(Event.TerminalApplied, scenario == "rejected" ? Outcome.Failed : Outcome.Sent));
        Assert.Equal(scenario is "accepted" or "rejected" ? 1 : 0, Count(Event.ProcessingFinished, Outcome.TerminalPathComplete));
        Assert.Equal(scenario == "journal_error" ? 1 : 0, Count(Event.AfterTerminalApplied, Outcome.Error));
        if (scenario is "journal_error" or "unknown_commit" or "timeout") Assert.NotNull(exception);
        else Assert.Null(exception);
        // A committed but unacknowledged result deliberately leaves a DB/telemetry difference of one.
        Assert.Equal(scenario == "unknown_commit" ? 1 : 0, (committed ? 1 : 0) - (confirmed ? 1 : 0));
    }

    public class CommitObservationProxy : DispatchProxy
    {
        public IDeliveryRepository Target = null!;
        public string Scenario = "";
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IDeliveryRepository.TryCompleteAsync))
            {
                if (Scenario == "stale") return Task.FromResult(false);
                var task = (Task<bool>)method.Invoke(Target, args)!;
                return Scenario == "unknown_commit" ? LoseAcknowledgement(task) : task;
            }
            return method.Invoke(Target, args);
        }
        private static async Task<bool> LoseAcknowledgement(Task<bool> commit)
        {
            Assert.True(await commit);
            throw new IOException("injected_lost_acknowledgement");
        }
    }

    private sealed class FailingTerminalJournal(IDeliveryStatusJournal inner) : IDeliveryStatusJournal
    {
        public Task AppendAsync(StatusChangeEntry entry, CancellationToken ct) =>
            entry.NewStatus is DeliveryStatus.Sent or DeliveryStatus.Failed
                ? throw new IOException("injected_terminal_journal_failure") : inner.AppendAsync(entry, ct);
    }
}
