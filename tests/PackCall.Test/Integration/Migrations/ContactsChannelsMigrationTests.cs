using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Repositories;

namespace PackCall.Core.Test;

[Trait("Category", "Integration")]
[Trait("Module", "Migrations")]
public sealed class ContactsChannelsMigrationTests : IAsyncLifetime
{
    private const string Initial = "20261028193126_InitialCreate";
    private const string Candidate = "20261113214910_ContactsChannels";
    private readonly string _schema = "migration_test_" + Guid.NewGuid().ToString("N");
    private readonly string _server = Environment.GetEnvironmentVariable("PACKCALL_TEST_POSTGRES")
        ?? throw new InvalidOperationException("PACKCALL_TEST_POSTGRES must be set.");

    private string ConnectionString => new NpgsqlConnectionStringBuilder(_server)
        { SearchPath = _schema }.ConnectionString;

    public async Task InitializeAsync()
    {
        await SqlAsync($"""CREATE SCHEMA "{_schema}" """, _server);
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync(Initial);
    }

    public async Task DisposeAsync() => await SqlAsync($"""DROP SCHEMA IF EXISTS "{_schema}" CASCADE""", _server);

    [Fact]
    public async Task PreservesHistoryAndSnapshotsAndCanBeAppliedAgain()
    {
        await SqlAsync("""
            INSERT INTO recipients (id, email, created_at, updated_at) VALUES
                ('00000000-0000-0000-0000-000000000001', ' New@Example.test ', '2026-01-01Z', '2026-02-01Z'),
                ('00000000-0000-0000-0000-000000000002', 'empty@example.test', NULL, NULL);
            INSERT INTO campaigns (id, name, message_text, status) VALUES
                ('00000000-0000-0000-0000-000000000003', 'history', 'message', 1),
                ('00000000-0000-0000-0000-000000000004', 'empty', 'message', 0);
            INSERT INTO deliveries (id, campaign_id, recipient_id, recipient_email, delivery_status, created_at, updated_at, worker_id, lock_expires_at)
            VALUES
                ('00000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000001',' Old@Example.test ',1,'2026-01-01Z','2026-02-01Z','worker','2026-03-01Z'),
                ('00000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000002','',0,'2026-01-01Z','2026-02-01Z',NULL,NULL);
            INSERT INTO delivery_status_journal (campaign_id,delivery_id,previous_status,new_status,occurred_at)
            VALUES ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000005',0,1,'2026-02-01Z');
            """);

        await using var db = Context();
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();

        Assert.Equal(new[] { Initial, Candidate }, await db.Database.GetAppliedMigrationsAsync());
        var deliveries = await db.Deliveries.OrderBy(d => d.Id).ToArrayAsync();
        Assert.Equal(2, deliveries.Length);
        var historical = deliveries[0];
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000005"), historical.Id);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), historical.ContactId);
        Assert.Equal("email", historical.Channel);
        Assert.Equal("old@example.test", historical.AddressSnapshot);
        Assert.Equal(1, (int)historical.DeliveryStatus);
        Assert.Equal("worker", historical.WorkerId);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), historical.LockExpiresAt);
        Assert.Null(deliveries[1].AddressSnapshot);
        Assert.Equal(2, await db.Contacts.CountAsync());
        Assert.Equal("new@example.test", await db.ContactChannels.Where(c => c.ContactId == historical.ContactId).Select(c => c.Address).SingleAsync());
        Assert.Equal(deliveries.Select(d => d.ContactId), await db.CampaignContacts.Where(c => c.CampaignId == historical.CampaignId).OrderBy(c => c.ContactId).Select(c => c.ContactId).ToArrayAsync());
        var channel = await db.CampaignChannels.SingleAsync();
        Assert.Equal((historical.CampaignId, "email"), (channel.CampaignId, channel.Channel));
        Assert.Equal(1L, await SqlAsync("SELECT count(*) FROM delivery_status_journal WHERE new_status=1"));
        Assert.Null(await SqlAsync("SELECT to_regclass('recipients')::text"));

        await RejectAsync("INSERT INTO contact_channels SELECT gen_random_uuid(), '00000000-0000-0000-0000-000000000002', 'email', 'new@example.test'", "23505");
        await RejectAsync("INSERT INTO contact_channels SELECT gen_random_uuid(), '00000000-0000-0000-0000-000000000001', 'email', 'unique@example.test'", "23505");
        await RejectAsync("INSERT INTO deliveries SELECT gen_random_uuid(), campaign_id, contact_id, address_snapshot, delivery_status, created_at, updated_at, completed_at, worker_id, lock_expires_at, channel FROM deliveries LIMIT 1", "23505");
        await RejectAsync("UPDATE deliveries SET channel='telegram' WHERE id='00000000-0000-0000-0000-000000000005'", "23503");
        await SqlAsync("""
            INSERT INTO campaign_channels (campaign_id, channel)
            VALUES ('00000000-0000-0000-0000-000000000003', 'telegram');
            INSERT INTO deliveries (id, campaign_id, contact_id, channel, address_snapshot, delivery_status, created_at, updated_at)
            VALUES (gen_random_uuid(), '00000000-0000-0000-0000-000000000003',
                '00000000-0000-0000-0000-000000000002', 'telegram', '12345', 0, now(), now());
            """);
        var repository = new DeliveryRepository(db, new MigrationContextFactory(ConnectionString));
        Assert.Empty(await repository.CaptureBatchAsync(10, "test-worker", TimeSpan.FromMinutes(1), CancellationToken.None));
    }

    [Theory]
    [InlineData(" DUPLICATE@example.test ", "duplicate@example.test", "normalized address collision")]
    [InlineData(" ", "valid@example.test", "empty address")]
    public async Task InvalidAddressesLeaveLegacySchemaAndRecordsIntact(string first, string second, string reason)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO recipients (id,email) VALUES (gen_random_uuid(),@first),(gen_random_uuid(),@second)", connection);
        command.Parameters.AddWithValue("first", first);
        command.Parameters.AddWithValue("second", second);
        await command.ExecuteNonQueryAsync();

        await using var db = Context();
        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
        Assert.Equal($"Contact migration rejected: {reason}", exception.MessageText);
        Assert.Equal(new[] { Initial }, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(2L, await SqlAsync("SELECT count(*) FROM recipients"));
        Assert.Null(await SqlAsync("SELECT to_regclass('contacts')::text"));
    }

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(ConnectionString).Options);

    private sealed class MigrationContextFactory(string connectionString) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString).Options);
    }

    private async Task RejectAsync(string sql, string state)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => SqlAsync(sql));
        Assert.Equal(state, exception.SqlState);
    }

    private async Task<object?> SqlAsync(string sql, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
