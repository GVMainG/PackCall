using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PackCall.Core.Repositorys;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;
using Serilog;

namespace PackCall.Core.Test;

public partial class CampaignPausePostgresTests
{
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
}
