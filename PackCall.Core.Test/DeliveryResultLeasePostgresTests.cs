using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;
using PackCall.Core.Services;
using PackCall.Infrastructure;
using System.Text.Json;

namespace PackCall.Core.Test;

public partial class CampaignPausePostgresTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static SendResult Result(bool success) => success ? SendResult.Success() : SendResult.Failure("declined");
    private async Task<Delivery> ReadDelivery(Guid id)
    {
        await using var scope = _provider!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Deliveries.AsNoTracking().SingleAsync(d => d.Id == id);
    }
    private static string Snapshot(Delivery d) => JsonSerializer.Serialize(new { d.Id, d.CampaignId, d.RecipientId, d.RecipientEmail, d.CreatedAt, d.DeliveryStatus, d.UpdatedAt, d.CompletedAt, d.WorkerId, d.LockExpiresAt });
    private async Task<string> Journal(Guid id, NpgsqlConnection? observer = null)
    {
        await using var owned = observer is null ? new NpgsqlConnection(_connectionString) : null;
        var connection = observer ?? owned!;
        if (owned is not null) await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT coalesce(json_agg(j ORDER BY id)::text, '[]') FROM delivery_status_journal j WHERE delivery_id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private async Task AssertTerminal(Delivery original, bool success)
    {
        var stored = await ReadDelivery(original.Id);
        Assert.Equal(success ? DeliveryStatus.Sent : DeliveryStatus.Failed, stored.DeliveryStatus);
        Assert.NotNull(stored.CompletedAt);
        Assert.Equal(stored.CompletedAt, stored.UpdatedAt);
        Assert.Null(stored.WorkerId);
        Assert.Null(stored.LockExpiresAt);
        using var journal = JsonDocument.Parse(await Journal(original.Id));
        var entry = Assert.Single(journal.RootElement.EnumerateArray(), e => e.GetProperty("new_status").GetInt32() == (int)DeliveryStatus.Sent || e.GetProperty("new_status").GetInt32() == (int)DeliveryStatus.Failed);
        Assert.Equal((int)DeliveryStatus.InProgress, entry.GetProperty("previous_status").GetInt32());
        Assert.Equal((int)stored.DeliveryStatus, entry.GetProperty("new_status").GetInt32());
        Assert.Equal("worker-A", entry.GetProperty("worker_id").GetString());
        Assert.Equal(stored.CompletedAt, entry.GetProperty("occurred_at").GetDateTimeOffset().UtcDateTime);
        Assert.Equal(original.CreatedAt, stored.CreatedAt);
        Assert.Equal(original.RecipientEmail, stored.RecipientEmail);
        Assert.Equal(success ? "AcceptedByProvider" : "declined", entry.GetProperty("operator_result").GetString());
    }

    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Result_ActiveLease_CompletesWithJournal(bool success)
    {
        var campaignId = await CreateStartedCampaignAsync();
        await using var scope = _provider!.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var delivery = Assert.Single(await service.CaptureBatchAsync(1, "worker-A", TimeSpan.FromMinutes(2), CancellationToken.None));
        _sender.Handler = _ => Result(success);
        await service.ProcessAsync(delivery, CancellationToken.None);
        await AssertTerminal(delivery, success);
        await using var fresh = _provider!.CreateAsyncScope();
        Assert.Equal(CampaignStatus.Completed, (await fresh.ServiceProvider.GetRequiredService<ICampaignService>().GetCampaignAsync(campaignId, CancellationToken.None))!.Status);
    }

    [PostgresTheory]
    [InlineData(true, "waiting", false)] [InlineData(false, "waiting", false)]
    [InlineData(true, "captured", false)] [InlineData(false, "captured", false)]
    [InlineData(true, "captured", true)] [InlineData(false, "captured", true)]
    [InlineData(true, "sent", false)] [InlineData(false, "sent", false)]
    [InlineData(true, "sent", true)] [InlineData(false, "sent", true)]
    [InlineData(true, "failed", false)] [InlineData(false, "failed", false)]
    [InlineData(true, "failed", true)] [InlineData(false, "failed", true)]
    [InlineData(true, "owner", false)] [InlineData(false, "owner", false)]
    [InlineData(true, "expired", false)] [InlineData(false, "expired", false)]
    public async Task Result_StaleLease_DoesNotChangeRowOrJournal(bool success, string state, bool sameWorker)
    {
        await CreateStartedCampaignAsync();
        await using var oldScope = _provider!.CreateAsyncScope();
        var oldRepository = oldScope.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        var oldService = oldScope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var now = DateTime.UtcNow;
        var old = Assert.Single(await oldRepository.CaptureBatchAsync(1, "worker-A", TimeSpan.FromSeconds(state == "expired" ? 0.05 : 30), CancellationToken.None));
        // Проверяем отслеживаемый снимок и последующий SaveChanges того же контекста.
        old = Assert.Single(await oldRepository.Get(old.Id, CancellationToken.None));
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.AsyncHandler = (_, _) => { sending.TrySetResult(); return release.Task; };
        var late = oldService.ProcessAsync(old, CancellationToken.None);
        await sending.Task.WaitAsync(Timeout);
        await using var nextScope = _provider!.CreateAsyncScope();
        var nextRepository = nextScope.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        var nextService = nextScope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        Delivery? next = null;
        if (state == "owner")
        {
            await nextScope.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET worker_id = {"worker-B"} WHERE id = {old.Id}");
            var changed = await ReadDelivery(old.Id);
            Assert.Equal(old.LockExpiresAt, changed.LockExpiresAt);
            Assert.NotEqual(old.WorkerId, changed.WorkerId);
        }
        else if (state != "expired")
        {
            await nextScope.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET lock_expires_at = clock_timestamp() - interval '1 second' WHERE id = {old.Id}");
            Assert.Single(await nextRepository.ReleaseExpiredLocksAsync(CancellationToken.None));
            if (state != "waiting")
            {
                next = Assert.Single(await nextRepository.CaptureBatchAsync(1, sameWorker ? "worker-A" : "worker-B", TimeSpan.FromMinutes(2), CancellationToken.None));
                Assert.NotEqual(old.LockExpiresAt, next.LockExpiresAt);
                if (state is "sent" or "failed")
                {
                    _sender.AsyncHandler = null;
                    _sender.Handler = _ => Result(state == "sent");
                    await nextService.ProcessAsync(next, CancellationToken.None);
                }
            }
        }
        if (state == "expired")
            await Task.Delay(100);
        var before = Snapshot(await ReadDelivery(old.Id));
        var history = await Journal(old.Id);
        release.SetResult(Result(success));
        await late.WaitAsync(Timeout);
        await oldScope.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync();
        Assert.Equal(before, Snapshot(await ReadDelivery(old.Id)));
        Assert.Equal(history, await Journal(old.Id));
        if (state == "captured")
        {
            _sender.AsyncHandler = null;
            _sender.Handler = _ => SendResult.Success();
            await nextService.ProcessAsync(next!, CancellationToken.None);
            Assert.Equal(DeliveryStatus.Sent, (await ReadDelivery(old.Id)).DeliveryStatus);
        }
    }
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Result_StorageErrorOrCancellation_DetachesBeforeSqlFailure(bool cancelBlocked)
    {
        await CreateStartedCampaignAsync();
        await using var scope = _provider!.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        await context.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)context.Database.GetDbConnection()).ProcessID;
        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync();
        await using var observer = new NpgsqlConnection(_connectionString);
        await observer.OpenAsync();
        var captured = Assert.Single(await repository.CaptureBatchAsync(1, "worker-A", TimeSpan.FromMinutes(2), CancellationToken.None));
        var tracked = Assert.Single(await repository.Get(captured.Id, CancellationToken.None));
        Assert.Equal(EntityState.Unchanged, context.Entry(tracked).State);
        var before = Snapshot(tracked);
        var history = await Journal(tracked.Id, observer);

        if (cancelBlocked)
        {
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var rowLock = new NpgsqlCommand("SELECT id FROM deliveries WHERE id = @id FOR UPDATE", blocker, transaction))
            {
                rowLock.Parameters.AddWithValue("id", tracked.Id);
                await rowLock.ExecuteScalarAsync();
            }
            using var cancellation = new CancellationTokenSource();
            var processing = service.ProcessAsync(tracked, cancellation.Token);
            await WaitBlocked(pid, observer);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(Timeout));
            await transaction.RollbackAsync();
        }
        else
        {
            // Ограничение временно отвергает terminal UPDATE на стороне PostgreSQL.
            await using var constraint = new NpgsqlCommand(
                "ALTER TABLE deliveries ADD CONSTRAINT reject_terminal CHECK (delivery_status <> 2)", blocker);
            await constraint.ExecuteNonQueryAsync();
            var error = await Assert.ThrowsAsync<PostgresException>(() => service.ProcessAsync(tracked, CancellationToken.None).WaitAsync(Timeout));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            // Убираем причину ошибки до SaveChanges: оставшийся tracked-снимок теперь
            // смог бы записать ложный terminal и сделать проверку красной.
            constraint.CommandText = "ALTER TABLE deliveries DROP CONSTRAINT reject_terminal";
            await constraint.ExecuteNonQueryAsync();
        }

        await context.SaveChangesAsync(CancellationToken.None);
        Assert.Equal(before, Snapshot(await ReadDelivery(tracked.Id)));
        Assert.Equal(history, await Journal(tracked.Id));
        Assert.Equal(EntityState.Detached, context.Entry(tracked).State);
    }

    [PostgresFact]
    public async Task Result_CompletesBeforeExpiry_RecoveryWaitingOnCommitCannotRequeueTerminal()
    {
        await CreateStartedCampaignAsync();
        await using var completing = _provider!.CreateAsyncScope();
        await using var recovering = _provider!.CreateAsyncScope();
        var completeRepository = completing.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        var completeContext = completing.ServiceProvider.GetRequiredService<AppDbContext>();
        var recoveryContext = recovering.ServiceProvider.GetRequiredService<AppDbContext>();
        await completeContext.Database.OpenConnectionAsync();
        await recoveryContext.Database.OpenConnectionAsync();
        await using var observer = new NpgsqlConnection(_connectionString);
        await observer.OpenAsync();
        var delivery = Assert.Single(await completeRepository.CaptureBatchAsync(1, "worker-A", TimeSpan.FromSeconds(3), CancellationToken.None));
        var expiry = delivery.LockExpiresAt!.Value;
        var recoveryPid = ((NpgsqlConnection)recoveryContext.Database.GetDbConnection()).ProcessID;
        await using var transaction = await completeContext.Database.BeginTransactionAsync();
        await completing.ServiceProvider.GetRequiredService<IDeliveryProcessingService>().ProcessAsync(delivery, CancellationToken.None).WaitAsync(Timeout);
        {
            await using var wait = new NpgsqlCommand("SELECT pg_sleep(greatest(0, extract(epoch FROM (@expiry - clock_timestamp()))) + 0.02)", observer);
            wait.Parameters.AddWithValue("expiry", expiry);
            await wait.ExecuteNonQueryAsync();
        }
        var recovery = recovering.ServiceProvider.GetRequiredService<IDeliveryRepository>()
            .ReleaseExpiredLocksAsync(CancellationToken.None);
        await WaitBlocked(recoveryPid, observer);
        await transaction.CommitAsync();
        Assert.Empty(await recovery.WaitAsync(Timeout));
        await AssertTerminal(delivery, true);
        Assert.Empty(await completeRepository.CaptureBatchAsync(1, "worker-B", TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    private async Task<DateTime> DatabaseNow(NpgsqlConnection? observer = null)
    {
        await using var owned = observer is null ? new NpgsqlConnection(_connectionString) : null;
        var connection = observer ?? owned!;
        if (owned is not null) await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        return (DateTime)(await query.ExecuteScalarAsync())!;
    }

    private async Task WaitBlocked(int pid, NpgsqlConnection? observer = null)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        await using var owned = observer is null ? new NpgsqlConnection(_connectionString) : null;
        var connection = observer ?? owned!;
        if (owned is not null) await connection.OpenAsync(deadline.Token);
        while (true)
        {
            await using var query = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids(@pid)) > 0", connection);
            query.Parameters.AddWithValue("pid", pid);
            if ((bool)(await query.ExecuteScalarAsync(deadline.Token))!) return;
            await Task.Delay(10, deadline.Token);
        }
    }

    [PostgresTheory]
    [InlineData("double")]
    [InlineData("recovery")]
    [InlineData("expiry")]
    public async Task Result_ConcurrentSql_RespectsWinnerAndTimeAfterLock(string mode)
    {
        await CreateStartedCampaignAsync();
        await using var a = _provider!.CreateAsyncScope();
        await using var b = _provider!.CreateAsyncScope();
        var ra = a.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        var rb = b.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        var sa = a.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var sb = b.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var ca = a.ServiceProvider.GetRequiredService<AppDbContext>();
        var cb = b.ServiceProvider.GetRequiredService<AppDbContext>();
        await ca.Database.OpenConnectionAsync();
        await cb.Database.OpenConnectionAsync();
        var pidA = ((NpgsqlConnection)ca.Database.GetDbConnection()).ProcessID;
        var pidB = ((NpgsqlConnection)cb.Database.GetDbConnection()).ProcessID;
        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync();
        await using var observer = new NpgsqlConnection(_connectionString);
        await observer.OpenAsync();
        var original = Assert.Single(await ra.CaptureBatchAsync(1, "worker-A", TimeSpan.FromSeconds(mode == "double" ? 60 : 4), CancellationToken.None));
        var second = Assert.Single(await rb.Get(original.Id, CancellationToken.None));
        var before = Snapshot(original);
        var history = await Journal(original.Id, observer);
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var rowLock = new NpgsqlCommand("SELECT id FROM deliveries WHERE id = @id FOR UPDATE", blocker, transaction))
        {
            rowLock.Parameters.AddWithValue("id", original.Id);
            await rowLock.ExecuteScalarAsync();
        }
        var count = 0;
        _sender.Handler = _ => Result(Interlocked.Increment(ref count) == 1);
        var first = sa.ProcessAsync(original, CancellationToken.None);
        await WaitBlocked(pidA, observer);
        Task? concurrent = null;
        if (mode == "double")
        {
            concurrent = sb.ProcessAsync(second, CancellationToken.None);
            await WaitBlocked(pidB, observer);
        }
        else
        {
            // Проверка и ожидание используют часы PostgreSQL, а не часы приложения.
            await using var wait = new NpgsqlCommand("SELECT @expiry > clock_timestamp()", blocker, transaction);
            wait.Parameters.AddWithValue("expiry", original.LockExpiresAt ?? second.LockExpiresAt!.Value);
            Assert.True((bool)(await wait.ExecuteScalarAsync())!);
            wait.CommandText = "SELECT pg_sleep(greatest(0, extract(epoch FROM (@expiry - clock_timestamp()))) + 0.02)";
            await wait.ExecuteNonQueryAsync();
            if (mode == "recovery")
            {
                concurrent = rb.ReleaseExpiredLocksAsync(CancellationToken.None);
                await WaitBlocked(pidB, observer);
            }
        }
        await transaction.CommitAsync();
        await first.WaitAsync(Timeout);
        if (concurrent is not null) await concurrent.WaitAsync(Timeout);
        if (mode == "double")
        {
            var stored = await ReadDelivery(original.Id);
            await AssertTerminal(original, stored.DeliveryStatus == DeliveryStatus.Sent);
        }
        else if (mode == "expiry")
        {
            Assert.Equal(before, Snapshot(await ReadDelivery(original.Id)));
            Assert.Equal(history, await Journal(original.Id));
        }
        else
        {
            Assert.Equal(DeliveryStatus.Waiting, (await ReadDelivery(original.Id)).DeliveryStatus);
            Assert.Equal(history, await Journal(original.Id));
            var next = Assert.Single(await rb.CaptureBatchAsync(1, "worker-B", TimeSpan.FromMinutes(2), CancellationToken.None));
            _sender.Handler = _ => SendResult.Success();
            await sb.ProcessAsync(next, CancellationToken.None);
            Assert.Equal(DeliveryStatus.Sent, (await ReadDelivery(original.Id)).DeliveryStatus);
        }
    }

}

internal sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PACKCALL_TEST_POSTGRES")))
            Skip = "Задайте PACKCALL_TEST_POSTGRES для проверки на PostgreSQL.";
    }
}
