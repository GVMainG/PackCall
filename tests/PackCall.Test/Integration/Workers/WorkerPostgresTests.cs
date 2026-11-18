using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PackCall.Core.Entities;
using PackCall.Core.Journaling;
using PackCall.Core.Repositorys;
using PackCall.Core.Services;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;
using Serilog;

namespace PackCall.Core.Test;

[Collection("Measurement")]
[Trait("Category", "Integration")]
[Trait("Module", "Workers")]
public class WorkerPostgresTests : PostgresTestBase
{
    [PostgresTheory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Recovery_ReconcilesTerminalDeliveriesOnlyForActiveCampaigns(bool paused, bool accepted)
    {
        var id = await CreateStartedCampaignAsync();
        _sender.Handler = _ => Result(accepted);
        Guid deliveryId;
        await using (var scope = _provider!.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var processor = new DeliveryProcessingService(
                services.GetRequiredService<IDeliveryRepository>(),
                services.GetRequiredService<ICampaingRepository>(),
                _ => _sender,
                new FaultAfterTerminalJournal(services.GetRequiredService<IDeliveryStatusJournal>()));
            var delivery = Assert.Single(await processor.CaptureBatchAsync(1, "worker-A", TimeSpan.FromSeconds(90), default));
            deliveryId = delivery.Id;
            await Assert.ThrowsAsync<IOException>(() => processor.ProcessAsync(delivery, default));
            await AssertTerminal(delivery, accepted);

            var campaign = await services.GetRequiredService<ICampaingRepository>().Get(id, default);
            Assert.NotNull(campaign);
            Assert.Equal(CampaignStatus.InProgress, campaign.Status);
            Assert.Null(campaign.CompletedAt);
            if (paused)
                await services.GetRequiredService<ICampaignService>().PauseCampaignAsync(id, default);
        }

        var deliveryBefore = Snapshot(await ReadDelivery(deliveryId));
        var journalBefore = await Journal(deliveryId);
        Assert.Single(_sender.Requests);

        await using var recoveryScope = _provider!.CreateAsyncScope();
        var recovery = recoveryScope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var campaigns = recoveryScope.ServiceProvider.GetRequiredService<ICampaingRepository>();
        var before = (await campaigns.Get(id, default))!;
        Guid? pendingCampaignId = null;
        if (!paused)
        {
            var control = recoveryScope.ServiceProvider.GetRequiredService<ICampaignService>();
            var pending = await control.CreateCampaignAsync(
                "Ожидающая рассылка", "Сообщение", null, [ContactChannel.Email], default);
            pendingCampaignId = pending.Id;
            await control.StartCampaignAsync(pending.Id, default);
            var waiting = await recoveryScope.ServiceProvider.GetRequiredService<AppDbContext>().Deliveries
                .AsNoTracking().SingleAsync(delivery => delivery.CampaignId == pending.Id);
            Assert.Equal(DeliveryStatus.Waiting, waiting.DeliveryStatus);
        }

        var dbBeforeRecovery = await DatabaseNow();
        Assert.Equal(0, await recovery.ReleaseExpiredLocksAsync(default));
        var after = (await campaigns.Get(id, default))!;
        Assert.Equal(paused ? CampaignStatus.Paused : CampaignStatus.Completed, after.Status);
        Assert.NotNull(before.StartedAt);
        Assert.Equal(before.StartedAt, after.StartedAt);
        if (paused)
        {
            Assert.Null(after.CompletedAt);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        }
        else
        {
            Assert.NotNull(after.CompletedAt);
            Assert.Equal(after.CompletedAt, after.UpdatedAt);
            Assert.True(after.CompletedAt >= dbBeforeRecovery);
        }

        Assert.Equal(0, await recovery.ReleaseExpiredLocksAsync(default));
        var repeated = (await campaigns.Get(id, default))!;
        Assert.Equal(after.Status, repeated.Status);
        Assert.Equal(after.StartedAt, repeated.StartedAt);
        Assert.Equal(after.CompletedAt, repeated.CompletedAt);
        Assert.Equal(after.UpdatedAt, repeated.UpdatedAt);
        Assert.Equal(deliveryBefore, Snapshot(await ReadDelivery(deliveryId)));
        Assert.Equal(journalBefore, await Journal(deliveryId));
        Assert.Single(_sender.Requests);
        if (pendingCampaignId is { } waitingId)
        {
            var waitingCampaign = (await campaigns.Get(waitingId, default))!;
            Assert.Equal(CampaignStatus.InProgress, waitingCampaign.Status);
            Assert.Null(waitingCampaign.CompletedAt);
        }
    }

    [PostgresTheory]
    [InlineData(DeliveryStatus.Waiting, false)]
    [InlineData(DeliveryStatus.Waiting, true)]
    [InlineData(DeliveryStatus.InProgress, false)]
    [InlineData(DeliveryStatus.InProgress, true)]
    public async Task Recovery_KeepsCampaignActiveWithPendingDelivery(DeliveryStatus status, bool legacy)
    {
        var id = await CreateStartedCampaignAsync();
        await using var scope = _provider!.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (status == DeliveryStatus.InProgress)
            Assert.Single(await processor.CaptureBatchAsync(1, "worker-A", TimeSpan.FromSeconds(90), default));
        if (legacy)
            await context.Deliveries.Where(delivery => delivery.CampaignId == id)
                .ExecuteUpdateAsync(update => update.SetProperty(delivery => delivery.AddressSnapshot, (string?)null));

        var deliveryBefore = await context.Deliveries.AsNoTracking().SingleAsync(delivery => delivery.CampaignId == id);
        Assert.Equal(status, deliveryBefore.DeliveryStatus);
        Assert.Equal(legacy, deliveryBefore.AddressSnapshot is null);
        var journalBefore = await Journal(deliveryBefore.Id);

        Assert.Equal(0, await processor.ReleaseExpiredLocksAsync(default));
        var campaign = await scope.ServiceProvider.GetRequiredService<ICampaingRepository>().Get(id, default);
        Assert.NotNull(campaign);
        Assert.Equal(CampaignStatus.InProgress, campaign.Status);
        Assert.Null(campaign.CompletedAt);
        Assert.Equal(Snapshot(deliveryBefore), Snapshot(await ReadDelivery(deliveryBefore.Id)));
        Assert.Equal(journalBefore, await Journal(deliveryBefore.Id));
        Assert.Empty(_sender.Requests);
    }

    [PostgresFact]
    public async Task Capture_UsesOneDatabaseTimestamp_RecoveryPreservesLeaseReplacedWhileWaiting()
    {
        await CreateStartedCampaignAsync(2);
        await using var scope = _provider!.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Database.OpenConnectionAsync();
        var capturePid = ((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
        await using var captureBlocker = new NpgsqlConnection(_connectionString);
        await captureBlocker.OpenAsync();
        await using var captureTransaction = await captureBlocker.BeginTransactionAsync();
        await using (var tableLock = new NpgsqlCommand("LOCK TABLE deliveries IN ACCESS EXCLUSIVE MODE", captureBlocker, captureTransaction))
            await tableLock.ExecuteNonQueryAsync();
        var capture = repository.CaptureBatchAsync(2, "old", TimeSpan.FromSeconds(60), CancellationToken.None);
        await WaitBlocked(capturePid);
        var before = await DatabaseNow();
        await captureTransaction.CommitAsync();
        var batch = await capture.WaitAsync(Timeout);
        var after = await DatabaseNow();
        Assert.Equal(2, batch.Count);
        Assert.Single(batch.Select(d => d.LockExpiresAt).Distinct());
        Assert.All(batch, d => {
            Assert.InRange(d.UpdatedAt, before, after);
            Assert.Equal(d.UpdatedAt.AddSeconds(60), d.LockExpiresAt);
        });
        Assert.Empty(await repository.ReleaseExpiredLocksAsync(CancellationToken.None));
        await context.Database.ExecuteSqlRawAsync("UPDATE deliveries SET lock_expires_at = clock_timestamp() - interval '1 second'");
        await context.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand("SELECT id FROM deliveries ORDER BY id FOR UPDATE", blocker, transaction))
            await locked.ExecuteNonQueryAsync();
        var recovery = repository.ReleaseExpiredLocksAsync(CancellationToken.None);
        await WaitBlocked(pid);
        await using (var renewed = new NpgsqlCommand("UPDATE deliveries SET worker_id='new', lock_expires_at=clock_timestamp()+interval '60 seconds'", blocker, transaction))
            await renewed.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        Assert.Empty(await recovery.WaitAsync(Timeout));
        var rows = await context.Deliveries.AsNoTracking().ToListAsync();
        Assert.All(rows, d => Assert.Equal("new", d.WorkerId));
    }

    [PostgresFact]
    public async Task SchemaGate_WaitsForExactHistory_RejectsUnknown_ObservesCancellation()
    {
        var factory = _provider!.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var logger = new LoggerConfiguration().CreateLogger();
        var lifetime = new Lifetime();
        var gate = new SchemaGate(factory, lifetime, logger);
        using var deadline = new CancellationTokenSource(Timeout);
        var waiting = gate.StartingAsync(deadline.Token);
        Assert.False(waiting.IsCompleted);
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" varchar(150) PRIMARY KEY, \"ProductVersion\" varchar(32) NOT NULL)");
        foreach (var id in db.Database.GetMigrations())
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"__EFMigrationsHistory\" VALUES ({id}, '10.0.4')");
        await waiting.WaitAsync(Timeout);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" VALUES ('20990101000000_Unknown','10.0.4')");
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.StartingAsync(deadline.Token));
        await db.Database.ExecuteSqlRawAsync("TRUNCATE \"__EFMigrationsHistory\"");
        var cancelled = gate.StartingAsync(deadline.Token);
        lifetime.StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stop = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stop.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stop.Cancel();
    }

    private sealed class FaultAfterTerminalJournal(IDeliveryStatusJournal inner) : IDeliveryStatusJournal
    {
        public async Task AppendAsync(StatusChangeEntry entry, CancellationToken ct)
        {
            await inner.AppendAsync(entry, ct);
            if (entry.NewStatus is DeliveryStatus.Sent or DeliveryStatus.Failed)
                throw new IOException("injected_after_terminal_journal");
        }
    }
}
