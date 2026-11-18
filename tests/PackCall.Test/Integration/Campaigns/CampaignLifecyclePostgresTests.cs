using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Repositorys;
using PackCall.Core.Services;
using PackCall.Infrastructure;

namespace PackCall.Core.Test;

[Trait("Category", "Integration")]
[Trait("Module", "Campaigns")]
public sealed class CampaignLifecyclePostgresTests : PostgresTestBase
{
    [PostgresFact]
    public async Task PublicCreationExposesForming_AndPublishesUniqueSelectedPairs()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        var ct = deadline.Token;
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        await using var writer = _provider!.CreateAsyncScope();
        await using var reader = _provider!.CreateAsyncScope();
        var campaigns = writer.ServiceProvider.GetRequiredService<ICampaignService>();
        var contacts = writer.ServiceProvider.GetRequiredService<IContactRepository>();
        var db = reader.ServiceProvider.GetRequiredService<AppDbContext>();
        var both = await contacts.CreateAsync(Contact.Create(
            [(ContactChannel.Email, "both@example.com"), (ContactChannel.Telegram, "42")], DateTime.UtcNow), ct);
        var email = await campaigns.CreateRecipientAsync("email@example.com", ct);
        await campaigns.CreateRecipientAsync("excluded@example.com", ct);

        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync(ct);
        await using var transaction = await blocker.BeginTransactionAsync(ct);
        await LockDeliveries(blocker, transaction, ct);
        var creation = client.PostAsJsonAsync("/api/campaigns", new
        {
            name = "Selected",
            messageText = "Text",
            selection = "contactIds",
            contactIds = new[] { both.Id, email.Id, both.Id },
            channels = new[] { "telegram", "email", "telegram" }
        }, ct);
        Guid id;
        try
        {
            await WaitForDeliveryInsert(blocker, transaction, creation, ct);
            id = await db.Campaigns.Select(c => c.Id).SingleAsync(ct);
            var forming = await ReadDetails(client, id, ct);
            Assert.Equal("Forming", forming.GetProperty("campaign").GetProperty("status").GetString());
            AssertFormation(forming, 2, ["email", "telegram"], 0, null);
            using var start = await client.PostAsync($"/api/campaigns/{id}/start", null, ct);
            Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        using var created = await creation;
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/campaigns/{id}", created.Headers.Location?.AbsolutePath);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("Draft", body.GetProperty("status").GetString());
        var draft = await ReadDetails(client, id, ct);
        Assert.Equal("Draft", draft.GetProperty("campaign").GetProperty("status").GetString());
        AssertFormation(draft, 2, ["email", "telegram"], 3, 1);
        var snapshot = await db.CampaignContacts.Where(row => row.CampaignId == id)
            .Select(row => row.ContactId).ToListAsync(ct);
        Assert.Equal(new[] { both.Id, email.Id }.Order(), snapshot.Order());
        var selectedDeliveries = await db.Deliveries.Where(row => row.CampaignId == id).ToListAsync(ct);
        Assert.Equal(new (Guid ContactId, string Channel, string? Address)[]
        {
            (both.Id, "email", "both@example.com"),
            (both.Id, "telegram", "42"),
            (email.Id, "email", "email@example.com")
        }.Order(), selectedDeliveries.Select(row => (row.ContactId, row.Channel, row.AddressSnapshot)).Order());

        await contacts.ReplaceAddressesAsync(both.Id, [(ContactChannel.Email, "changed@example.com")], ct);
        var deliveryId = await db.Deliveries.Where(d => d.CampaignId == id && d.Channel == "telegram")
            .Select(d => d.Id).SingleAsync(ct);
        await db.Deliveries.Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(update => update.SetProperty(d => d.DeliveryStatus, DeliveryStatus.Sent), ct);
        var updated = await ReadDetails(client, id, ct);
        Assert.Equal(1, updated.GetProperty("deliveries").GetProperty("sent").GetInt32());
        AssertFormation(updated, 2, ["email", "telegram"], 3, 1);

        var emailId = (await campaigns.CreateCampaignAsync("Email", "Text", [email.Id], ["email"], ct)).Id;
        using var started = await client.PostAsync($"/api/campaigns/{emailId}/start", null, ct);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        var zeroId = (await campaigns.CreateCampaignAsync("Zero", "Text", [email.Id], ["telegram"], ct)).Id;
        var zero = await ReadDetails(client, zeroId, ct);
        Assert.Equal("Draft", zero.GetProperty("campaign").GetProperty("status").GetString());
        AssertFormation(zero, 1, ["telegram"], 0, 1);

        var legacy = Campaign.Create("Legacy", "Text");
        var repository = writer.ServiceProvider.GetRequiredService<ICampaingRepository>();
        await repository.Create(legacy, ct);
        AssertFormation(await ReadDetails(client, legacy.Id, ct), 0, [], 0, 0);

        var publicEmailId = await campaigns.CreateCampaignAsync("Public email", "Text", ct);
        await campaigns.StartCampaignAsync(publicEmailId, ct);
        await repository.DeleteIfFormingAsync(publicEmailId, ct);
        await writer.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(ct);
        Assert.Equal(CampaignStatus.InProgress, (await campaigns.GetCampaignAsync(publicEmailId, ct))!.Status);
        AssertFormation(await ReadDetails(client, publicEmailId, ct), 3, ["email"], 3, 0);
    }

    [PostgresFact]
    public async Task DetailsKeepTheSameSnapshotWhenPublicationCommitsDuringRead()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        var ct = deadline.Token;
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await campaigns.CreateRecipientAsync("email@example.com", ct);
        var id = await campaigns.CreateCampaignSnapshotAsync("Snapshot", "Text", [contact.Id], ["email"], ct);

        await using var writer = new NpgsqlConnection(_connectionString);
        await writer.OpenAsync(ct);
        await using var transaction = await writer.BeginTransactionAsync(ct);
        await using var publish = new NpgsqlCommand("""
            LOCK TABLE campaign_channels IN ACCESS EXCLUSIVE MODE;
            INSERT INTO deliveries (id, campaign_id, contact_id, channel, address_snapshot, delivery_status, created_at, updated_at)
            VALUES (gen_random_uuid(), @id, @contact, 'email', 'email@example.com', @waiting, now(), now());
            UPDATE campaigns SET status = @draft WHERE id = @id;
            """, writer, transaction);
        publish.Parameters.AddWithValue("id", id);
        publish.Parameters.AddWithValue("contact", contact.Id);
        publish.Parameters.AddWithValue("waiting", (int)DeliveryStatus.Waiting);
        publish.Parameters.AddWithValue("draft", (int)CampaignStatus.Draft);
        await publish.ExecuteNonQueryAsync(ct);

        var reading = ReadDetails(client, id, ct);
        await using var waiting = new NpgsqlCommand("""
            SELECT EXISTS (SELECT FROM pg_locks
                WHERE relation = 'campaign_channels'::regclass AND mode = 'AccessShareLock' AND NOT granted)
            """, writer, transaction);
        while (!(bool)(await waiting.ExecuteScalarAsync(ct))!)
        {
            Assert.False(reading.IsCompleted);
            await Task.Delay(10, ct);
        }
        await transaction.CommitAsync(ct);

        var before = await reading;
        Assert.Equal("Forming", before.GetProperty("campaign").GetProperty("status").GetString());
        AssertFormation(before, 1, ["email"], 0, null);
        var after = await ReadDetails(client, id, ct);
        Assert.Equal("Draft", after.GetProperty("campaign").GetProperty("status").GetString());
        AssertFormation(after, 1, ["email"], 1, 0);
    }

    [PostgresFact]
    public async Task FailedDeliveryInsertCompensates_AndPublicEmailPostReturnsGenericProblem()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await campaigns.CreateRecipientAsync("email@example.com", default);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_formation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'private insert failure'; END; $$;
            CREATE TRIGGER fail_delivery_insert BEFORE INSERT ON deliveries
            FOR EACH ROW EXECUTE FUNCTION fail_formation();
            """);

        var error = await Assert.ThrowsAsync<CampaignFormationException>(() =>
            campaigns.CreateCampaignAsync("Selected", "Text", [contact.Id], ["email"], default));
        Assert.Contains("private insert failure", Assert.IsType<PostgresException>(error.InnerException).Message);
        await AssertEmptyCampaigns(db);

        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        using var response = await client.PostAsJsonAsync("/api/campaigns",
            new { name = "Email", messageText = "Text", selection = "all", channels = new[] { "email" } });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.DoesNotContain("private insert failure", problem.GetRawText());
        Assert.False(problem.TryGetProperty("detail", out var detail) && detail.ValueKind != JsonValueKind.Null);
        await AssertEmptyCampaigns(db);
    }

    [PostgresFact]
    public async Task FailedPublicationCascades_AndFailedCleanupLeavesWaitingDeliveriesUnclaimable()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await campaigns.CreateRecipientAsync("email@example.com", default);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_campaign_write() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'private % failure', TG_OP; END; $$;
            CREATE TRIGGER fail_publication BEFORE UPDATE ON campaigns
            FOR EACH ROW EXECUTE FUNCTION fail_campaign_write();
            """);

        var publish = await Assert.ThrowsAsync<CampaignFormationException>(() =>
            campaigns.CreateCampaignAsync("Compensated", "Text", [contact.Id], ["email"], default));
        Assert.Contains("private UPDATE failure", Assert.IsType<PostgresException>(publish.InnerException).Message);
        await AssertEmptyCampaigns(db);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_compensation BEFORE DELETE ON campaigns
            FOR EACH ROW EXECUTE FUNCTION fail_campaign_write();
            """);
        var failed = await Assert.ThrowsAsync<CampaignFormationException>(() =>
            campaigns.CreateCampaignAsync("Retained", "Text", [contact.Id], ["email"], default));
        var causes = Assert.IsType<AggregateException>(failed.InnerException).InnerExceptions;
        Assert.Collection(causes,
            error => Assert.Contains("private UPDATE failure", Assert.IsType<PostgresException>(error).Message),
            error => Assert.Contains("private DELETE failure", Assert.IsType<PostgresException>(error).Message));
        var retained = await db.Campaigns.AsNoTracking().SingleAsync();
        Assert.Equal(CampaignStatus.Forming, retained.Status);

        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        using var response = await client.PostAsync($"/api/campaigns/{retained.Id}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var deliveries = scope.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        Assert.Empty(await deliveries.CaptureBatchAsync(1, "worker", TimeSpan.FromSeconds(60), default));
        Assert.Equal(DeliveryStatus.Waiting, (await db.Deliveries.SingleAsync()).DeliveryStatus);
        AssertFormation(await ReadDetails(client, retained.Id, default), 1, ["email"], 1, null);
    }

    [PostgresFact]
    public async Task CancellationBeforePublicationCompensatesWithIndependentToken()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await campaigns.CreateRecipientAsync("email@example.com", deadline.Token);
        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync(deadline.Token);
        await using var transaction = await blocker.BeginTransactionAsync(deadline.Token);
        await LockDeliveries(blocker, transaction, deadline.Token);

        var creation = campaigns.CreateCampaignAsync("Cancelled", "Text", [contact.Id], ["email"], cancel.Token);
        try
        {
            await WaitForDeliveryInsert(blocker, transaction, creation, deadline.Token);
            cancel.Cancel();
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation.WaitAsync(Timeout));
        await AssertEmptyCampaigns(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [PostgresFact]
    public async Task ConcurrentDeleteBeforePublicationFailsCreation()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        var ct = deadline.Token;
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await campaigns.CreateRecipientAsync("email@example.com", ct);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync(ct);
        await using var barrier = await blocker.BeginTransactionAsync(ct);
        await LockDeliveries(blocker, barrier, ct);
        await using var remover = new NpgsqlConnection(_connectionString);
        await remover.OpenAsync(ct);
        await using var deletion = await remover.BeginTransactionAsync(ct);

        var creation = campaigns.CreateCampaignAsync("Race", "Text", [contact.Id], ["email"], ct);
        Task<int> delete;
        try
        {
            await WaitForDeliveryInsert(blocker, barrier, creation, ct);
            await using var sql = new NpgsqlCommand("DELETE FROM campaigns WHERE status = @forming", remover, deletion);
            sql.Parameters.AddWithValue("forming", (int)CampaignStatus.Forming);
            delete = sql.ExecuteNonQueryAsync(ct);
            await WaitBlocked(remover.ProcessID, blocker);
            await barrier.RollbackAsync(ct);
            Assert.Equal(1, await delete);
        }
        catch
        {
            deadline.Cancel();
            throw;
        }

        await deletion.CommitAsync(ct);
        var error = await Assert.ThrowsAsync<CampaignFormationException>(() => creation);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        await AssertEmptyCampaigns(db);
    }

    private static async Task LockDeliveries(NpgsqlConnection blocker, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var sql = new NpgsqlCommand("LOCK TABLE deliveries IN SHARE MODE", blocker, transaction);
        await sql.ExecuteNonQueryAsync(ct);
    }

    private static async Task WaitForDeliveryInsert(NpgsqlConnection blocker, NpgsqlTransaction transaction,
        Task creation, CancellationToken ct)
    {
        await using var waiting = new NpgsqlCommand("""
            SELECT EXISTS (SELECT FROM pg_locks
                WHERE relation = 'deliveries'::regclass AND mode = 'RowExclusiveLock' AND NOT granted)
            """, blocker, transaction);
        while (!(bool)(await waiting.ExecuteScalarAsync(ct))!)
        {
            Assert.False(creation.IsCompleted);
            await Task.Delay(10, ct);
        }
    }

    private static async Task<JsonElement> ReadDetails(HttpClient client, Guid id, CancellationToken ct)
    {
        using var response = await client.GetAsync($"/api/campaigns/{id}", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private static void AssertFormation(JsonElement details, int contacts, string[] channels, int deliveries, long? skips)
    {
        var formation = details.GetProperty("formation");
        Assert.Equal(contacts, formation.GetProperty("selectedContactCount").GetInt32());
        Assert.Equal(channels, formation.GetProperty("channels").EnumerateArray().Select(channel => channel.GetString()));
        Assert.Equal(deliveries, formation.GetProperty("createdDeliveryCount").GetInt32());
        Assert.Equal(skips, formation.GetProperty("skippedDeliveryCount").Deserialize<long?>());
        Assert.Equal(deliveries, details.GetProperty("deliveries").GetProperty("total").GetInt32());
    }

    private static async Task AssertEmptyCampaigns(AppDbContext db)
    {
        Assert.False(await db.Campaigns.AnyAsync());
        Assert.False(await db.CampaignContacts.AnyAsync());
        Assert.False(await db.CampaignChannels.AnyAsync());
        Assert.False(await db.Deliveries.AnyAsync());
    }
}
