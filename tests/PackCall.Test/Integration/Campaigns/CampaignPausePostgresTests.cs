using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PackCall.Api.Controllers;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;
using PackCall.Core.Services;
using PackCall.Infrastructure;
using Serilog;

namespace PackCall.Core.Test;

// Set PACKCALL_TEST_POSTGRES to a test PostgreSQL connection string.
// Every test creates and removes its own schema; existing tables are never modified.
[Collection("Measurement")]
[Trait("Category", "Integration")]
[Trait("Module", "Campaigns")]
public class CampaignPausePostgresTests : PostgresTestBase
{
    [PostgresFact]
    public async Task MultipleBatchesPersistAllEmailContactsAndOneCampaignChannel()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var contacts = Enumerable.Range(0, 501)
            .Select(index => Contact.Create([(ContactChannel.Email, $"batch{index}@example.test")], DateTime.UtcNow))
            .ToArray();
        db.Contacts.AddRange(contacts);
        await db.SaveChangesAsync();

        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var campaignId = (await campaigns.CreateCampaignAsync(
            "Batch", "Message", null, [ContactChannel.Email], CancellationToken.None)).Id;
        var selected = await db.CampaignContacts.Where(c => c.CampaignId == campaignId).Select(c => c.ContactId).ToArrayAsync();
        Assert.Equal(contacts.Select(c => c.Id).Order(), selected.Order());
        Assert.Equal("email", await db.CampaignChannels.Where(c => c.CampaignId == campaignId).Select(c => c.Channel).SingleAsync());
        Assert.Equal(501, await db.Deliveries.CountAsync(d => d.CampaignId == campaignId));
    }

    [PostgresFact]
    public async Task DeliveryPagePreservesSnapshotAndRejectsOffsetOverflow()
    {
        var campaignId = await CreateStartedCampaignAsync(3);
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        var expected = await db.Deliveries.AsNoTracking().Where(d => d.CampaignId == campaignId)
            .OrderBy(d => d.Id).ToArrayAsync();

        var page = await repository.GetPage(campaignId, 2, 2, CancellationToken.None);
        var delivery = Assert.Single(page.Items);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(expected[2].Id, delivery.Id);
        Assert.Equal(expected[2].AddressSnapshot, delivery.AddressSnapshot);
        Assert.Empty(db.ChangeTracker.Entries());

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.GetPage(campaignId, int.MaxValue, 2, CancellationToken.None));
        Assert.Equal("pageNumber", error.Message);
        Assert.IsType<OverflowException>(error.InnerException);
    }

    [PostgresFact]
    public async Task PauseDuringBatch_AllowsCurrentSendReleasesTailAndResumesWithoutDuplicates()
    {
        var id = await CreateStartedCampaignAsync(4);
        await using var workerScope = _provider!.CreateAsyncScope();
        var processing = workerScope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var campaigns = workerScope.ServiceProvider.GetRequiredService<ICampaignService>();
        var startedAt = (await campaigns.GetCampaignAsync(id, CancellationToken.None))!.StartedAt;
        var before = await campaigns.GetDeliveriesAsync(id, 1, 10, CancellationToken.None);
        var batch = await processing.CaptureBatchAsync(3, "worker-1", TimeSpan.FromSeconds(90), CancellationToken.None);
        Assert.Equal(3, batch.Count);

        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgement = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.AsyncHandler = (_, _) =>
        {
            sending.TrySetResult();
            return acknowledgement.Task;
        };
        var inFlight = processing.ProcessAsync(batch[0], CancellationToken.None);
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var pauseScope = _provider!.CreateAsyncScope())
            await pauseScope.ServiceProvider.GetRequiredService<ICampaignService>().PauseCampaignAsync(id, CancellationToken.None);
        acknowledgement.SetResult(SendResult.Success());
        await inFlight.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DeliveryStatus.Sent, (await ReadDelivery(batch[0].Id)).DeliveryStatus);
        foreach (var delivery in batch.Skip(1))
            await processing.ProcessAsync(delivery, CancellationToken.None);

        foreach (var tail in batch.Skip(1))
        {
            var stored = await ReadDelivery(tail.Id);
            Assert.Equal(DeliveryStatus.Waiting, stored.DeliveryStatus);
            Assert.Null(stored.WorkerId);
            Assert.Null(stored.LockExpiresAt);
        }
        Assert.Single(_sender.Requests);
        Assert.Equal(CampaignStatus.Paused, (await campaigns.GetCampaignAsync(id, CancellationToken.None))!.Status);
        var counts = await campaigns.GetDeliveryStatusCountsAsync(id, CancellationToken.None);
        Assert.Equal(1, counts.Sent);
        Assert.Equal(3, counts.Waiting);
        Assert.Equal(0, counts.InProgress);
        Assert.Empty(await processing.CaptureBatchAsync(10, "worker-2", TimeSpan.FromSeconds(90), CancellationToken.None));

        await using (var resumeScope = _provider!.CreateAsyncScope())
            await resumeScope.ServiceProvider.GetRequiredService<ICampaignService>().ResumeCampaignAsync(id, CancellationToken.None);
        _sender.AsyncHandler = null;
        var remaining = await processing.CaptureBatchAsync(10, "worker-2", TimeSpan.FromSeconds(90), CancellationToken.None);
        Assert.Equal(3, remaining.Count);
        foreach (var delivery in remaining)
            await processing.ProcessAsync(delivery, CancellationToken.None);

        var completed = (await campaigns.GetCampaignAsync(id, CancellationToken.None))!;
        Assert.Equal(CampaignStatus.Completed, completed.Status);
        Assert.Equal(startedAt, completed.StartedAt);
        Assert.Equal(4, _sender.Requests.Count);
        Assert.Equal(before.Items.Select(delivery => delivery.Id).Order(),
            _sender.Requests.Select(request => request.DeliveryId).Order());
    }

    [PostgresFact]
    public async Task PauseOnLastDelivery_ResumeCompletesWithoutSendingAgain()
    {
        var id = await CreateStartedCampaignAsync();
        await using var scope = _provider!.CreateAsyncScope();
        var processing = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var startedAt = (await campaigns.GetCampaignAsync(id, CancellationToken.None))!.StartedAt;
        var delivery = Assert.Single(await processing.CaptureBatchAsync(1, "worker-A", TimeSpan.FromMinutes(2), CancellationToken.None));
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.AsyncHandler = (_, _) => { sending.TrySetResult(); return release.Task; };
        var operation = processing.ProcessAsync(delivery, CancellationToken.None);
        await sending.Task.WaitAsync(Timeout);
        await using var other = _provider!.CreateAsyncScope();
        var control = other.ServiceProvider.GetRequiredService<ICampaignService>();
        await control.PauseCampaignAsync(id, CancellationToken.None);
        release.SetResult(SendResult.Success());
        await operation.WaitAsync(Timeout);
        Assert.Equal(CampaignStatus.Paused, (await control.GetCampaignAsync(id, CancellationToken.None))!.Status);
        Assert.Equal(DeliveryStatus.Sent, (await ReadDelivery(delivery.Id)).DeliveryStatus);
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        using var resumed = await client.PostAsync($"/api/campaigns/{id}/resume", null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var body = await resumed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.Equal(startedAt, body.GetProperty("startedAt").GetDateTime());
        Assert.True(body.GetProperty("completedAt").GetDateTime() >= startedAt);
        Assert.Single(_sender.Requests);
        Assert.Empty(await processing.CaptureBatchAsync(1, "worker-B", TimeSpan.FromMinutes(2), CancellationToken.None));
    }

    [PostgresFact]
    public async Task ConcurrentResumesCompleteWhenLastDeliveryFinishesWhileResumeIsBlocked()
    {
        using var deadline = new CancellationTokenSource(Timeout);
        var ct = deadline.Token;
        var id = await CreateStartedCampaignAsync();
        await using var worker = _provider!.CreateAsyncScope();
        var processing = worker.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var campaigns = worker.ServiceProvider.GetRequiredService<ICampaignService>();
        var startedAt = (await campaigns.GetCampaignAsync(id, ct))!.StartedAt;
        var delivery = Assert.Single(await processing.CaptureBatchAsync(1, "worker", TimeSpan.FromMinutes(1), ct));
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgement = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.AsyncHandler = (_, _) =>
        {
            sending.SetResult();
            return acknowledgement.Task;
        };
        var inFlight = processing.ProcessAsync(delivery, ct);
        await sending.Task.WaitAsync(ct);
        await campaigns.PauseCampaignAsync(id, ct);

        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync(ct);
        await using var transaction = await blocker.BeginTransactionAsync(ct);
        await using var lockCommand = new NpgsqlCommand("SELECT id FROM campaigns WHERE id = @id FOR UPDATE",
            blocker, transaction);
        lockCommand.Parameters.AddWithValue("id", id);
        Assert.Equal(id, await lockCommand.ExecuteScalarAsync(ct));
        await using var first = _provider!.CreateAsyncScope();
        var firstDb = first.ServiceProvider.GetRequiredService<AppDbContext>();
        await firstDb.Database.OpenConnectionAsync(ct);
        var firstResume = first.ServiceProvider.GetRequiredService<ICampaignService>().ResumeCampaignAsync(id, ct);
        await WaitBlocked(((NpgsqlConnection)firstDb.Database.GetDbConnection()).ProcessID);

        acknowledgement.SetResult(SendResult.Success());
        await inFlight.WaitAsync(ct);
        Assert.Equal(DeliveryStatus.Sent, (await ReadDelivery(delivery.Id)).DeliveryStatus);

        await using var second = _provider!.CreateAsyncScope();
        var secondDb = second.ServiceProvider.GetRequiredService<AppDbContext>();
        await secondDb.Database.OpenConnectionAsync(ct);
        var secondResume = second.ServiceProvider.GetRequiredService<ICampaignService>().ResumeCampaignAsync(id, ct);
        await WaitBlocked(((NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID);
        await transaction.CommitAsync(ct);

        Assert.All(await Task.WhenAll(firstResume, secondResume).WaitAsync(ct), result =>
        {
            Assert.Equal(CampaignStatus.Completed, result.Status);
            Assert.Equal(startedAt, result.StartedAt);
            Assert.NotNull(result.CompletedAt);
        });
        var stored = Assert.IsType<Campaign>(await campaigns.GetCampaignAsync(id, ct));
        Assert.Equal(CampaignStatus.Completed, stored.Status);
        Assert.Equal(startedAt, stored.StartedAt);
        Assert.NotNull(stored.CompletedAt);
    }

    [PostgresFact]
    public async Task StatusUpdates_CannotOverwriteConcurrentPauseOrCompletion()
    {
        var id = await CreateStartedCampaignAsync();
        await using var first = _provider!.CreateAsyncScope();
        await using var second = _provider!.CreateAsyncScope();
        var firstRepository = first.ServiceProvider.GetRequiredService<ICampaingRepository>();
        var secondRepository = second.ServiceProvider.GetRequiredService<ICampaingRepository>();
        var paused = Assert.IsType<Campaign>(await firstRepository.Get(id, CancellationToken.None));
        var completed = Assert.IsType<Campaign>(await secondRepository.Get(id, CancellationToken.None));
        paused.StatusFromInProgressToPaused(DateTime.UtcNow);
        completed.StatusFromInProgressToCompleted(DateTime.UtcNow);

        var results = await Task.WhenAll(
            firstRepository.TryUpdateStatusAsync(paused, CampaignStatus.InProgress, CancellationToken.None),
            secondRepository.TryUpdateStatusAsync(completed, CampaignStatus.InProgress, CancellationToken.None));

        Assert.Single(results, updated => updated);
        var current = Assert.IsType<Campaign>(await firstRepository.Get(id, CancellationToken.None));
        Assert.Equal(results[0] ? CampaignStatus.Paused : CampaignStatus.Completed, current.Status);
        Assert.False(await firstRepository.TryUpdateStatusAsync(results[0] ? completed : paused,
            CampaignStatus.InProgress, CancellationToken.None));
    }

    [PostgresFact]
    public async Task HistoricalDeliveriesWithoutAddressStillCountAsPending()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var contact = await campaigns.CreateRecipientAsync("historical@example.com", default);
        var id = (await campaigns.CreateCampaignAsync("Historical", "Text", [contact.Id], ["email"], default)).Id;

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET address_snapshot = NULL WHERE campaign_id = {id}");
        await campaigns.StartCampaignAsync(id, default);
        var started = Assert.IsType<Campaign>(await campaigns.GetCampaignAsync(id, default));
        Assert.Equal(CampaignStatus.InProgress, started.Status);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE deliveries SET delivery_status = {(int)DeliveryStatus.InProgress}, worker_id = 'historical',
                lock_expires_at = clock_timestamp() + interval '1 minute'
            WHERE campaign_id = {id}
            """);
        await campaigns.PauseCampaignAsync(id, default);
        await campaigns.ResumeCampaignAsync(id, default);
        var resumed = Assert.IsType<Campaign>(await campaigns.GetCampaignAsync(id, default));
        Assert.Equal(CampaignStatus.InProgress, resumed.Status);
        Assert.Equal(started.StartedAt, resumed.StartedAt);
        var delivery = await db.Deliveries.AsNoTracking().SingleAsync();
        Assert.Equal(DeliveryStatus.InProgress, delivery.DeliveryStatus);
        Assert.Null(delivery.AddressSnapshot);
    }

    [PostgresFact]
    public async Task ReleaseCapture_CannotReleaseNewLeaseEvenForTheSameWorker()
    {
        await CreateStartedCampaignAsync();
        await using var scope = _provider!.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        var now = DateTime.UtcNow;
        var previous = Assert.Single(await repository.CaptureBatchAsync(1, "worker-1", TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Empty(await repository.CaptureBatchAsync(1, "worker-2", TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Empty(await repository.ReleaseExpiredLocksAsync(CancellationToken.None));
        var later = now.AddSeconds(11);
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE deliveries SET lock_expires_at = clock_timestamp() - interval '1 second'");
        Assert.Single(await repository.ReleaseExpiredLocksAsync(CancellationToken.None));
        var current = Assert.Single(await repository.CaptureBatchAsync(1, "worker-1", TimeSpan.FromSeconds(90), CancellationToken.None));

        Assert.Null(await repository.ReleaseCapturedAsync(previous.Id, "worker-1", previous.LockExpiresAt!.Value, later, CancellationToken.None));
        var stored = Assert.IsType<Delivery>(await repository.Get(current.Id, CancellationToken.None));
        Assert.Equal(DeliveryStatus.InProgress, stored.DeliveryStatus);
        Assert.Equal(current.LockExpiresAt, stored.LockExpiresAt);

        var released = await repository.ReleaseCapturedAsync(current.Id, "worker-1", current.LockExpiresAt!.Value, later, CancellationToken.None);
        Assert.NotNull(released);
        Assert.Equal(DeliveryStatus.Waiting, released.DeliveryStatus);
        Assert.Null(released.WorkerId);
        Assert.Null(released.LockExpiresAt);
    }

    [PostgresFact]
    public async Task HttpPauseAndResume_ReturnStatesAndRejectInvalidOperations()
    {
        var id = await CreateStartedCampaignAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        AddServices(builder.Services);
        builder.Services.AddSingleton<Serilog.ILogger>(Log.Logger);
        builder.Services.AddControllers().AddApplicationPart(typeof(CampaignsController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddProblemDetails();
        // Register the production handler without changing its visibility for tests.
        var handler = typeof(CampaignsController).Assembly.GetType("PackCall.Api.Middleware.ApiExceptionHandler", throwOnError: true)!;
        builder.Services.AddSingleton(typeof(IExceptionHandler), handler);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapControllers();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        using var paused = await client.PostAsync($"/api/campaigns/{id}/pause", null);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var pausedBody = await paused.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Paused", pausedBody.GetProperty("status").GetString());
        using var repeatedPause = await client.PostAsync($"/api/campaigns/{id}/pause", null);
        Assert.Equal(HttpStatusCode.OK, repeatedPause.StatusCode);
        var repeatedBody = await repeatedPause.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(pausedBody.GetProperty("updatedAt").GetString(), repeatedBody.GetProperty("updatedAt").GetString());
        using var deletePaused = await client.DeleteAsync($"/api/campaigns/{id}");
        Assert.Equal(HttpStatusCode.Conflict, deletePaused.StatusCode);
        using var patchPaused = await client.PatchAsJsonAsync($"/api/campaigns/{id}", new { name = "Другой текст" });
        Assert.Equal(HttpStatusCode.Conflict, patchPaused.StatusCode);

        using var resumed = await client.PostAsync($"/api/campaigns/{id}/resume", null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var resumedBody = await resumed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("InProgress", resumedBody.GetProperty("status").GetString());
        Assert.Equal(pausedBody.GetProperty("startedAt").GetString(), resumedBody.GetProperty("startedAt").GetString());
        using var repeatedResume = await client.PostAsync($"/api/campaigns/{id}/resume", null);
        Assert.Equal(HttpStatusCode.OK, repeatedResume.StatusCode);
        foreach (var action in new[] { "pause", "resume" })
        {
            using var missing = await client.PostAsync($"/api/campaigns/{Guid.NewGuid()}/{action}", null);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var draft = await client.PostAsJsonAsync("/api/campaigns",
                new { name = "Черновик", messageText = "Текст", selection = "all", channels = new[] { "email" } });
            draft.EnsureSuccessStatusCode();
            var draftId = (await draft.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
            using var invalid = await client.PostAsync($"/api/campaigns/{draftId}/{action}", null);
            Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        }
    }
}

internal sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PACKCALL_TEST_POSTGRES")))
            Skip = "Set PACKCALL_TEST_POSTGRES to run against an isolated schema in PostgreSQL.";
    }
}
