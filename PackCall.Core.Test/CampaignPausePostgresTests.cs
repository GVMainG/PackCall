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
public class CampaignPausePostgresTests : IAsyncLifetime
{
    private readonly string _schema = "packcall_pause_test_" + Guid.NewGuid().ToString("N");
    private readonly FakeMessageSender _sender = new();
    private ServiceProvider? _provider;
    private string _connectionString = "";
    private bool _schemaCreated;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("PACKCALL_TEST_POSTGRES")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{_schema}\"", connection);
        await command.ExecuteNonQueryAsync();
        _schemaCreated = true;
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = _schema }.ConnectionString;

        var services = new ServiceCollection();
        AddServices(services);
        _provider = services.BuildServiceProvider();
        await using var context = await _provider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        if (_schemaCreated)
        {
            await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("PACKCALL_TEST_POSTGRES"));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private void AddServices(IServiceCollection services)
    {
        services.AddInfrastructure(_connectionString);
        services.AddSingleton<IMessageSender>(_sender);
        services.AddScoped<ICampaignService, CampaignService>();
        services.AddScoped<IDeliveryProcessingService, DeliveryProcessingService>();
    }

    private async Task<Guid> CreateStartedCampaignAsync(int recipients = 1)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        for (var i = 0; i < recipients; i++)
            await service.CreateRecipientAsync($"user{i}@example.com", CancellationToken.None);
        var id = await service.CreateCampaignAsync("Рассылка", "Сообщение", CancellationToken.None);
        await service.StartCampaignAsync(id, CancellationToken.None);
        return id;
    }

    [PostgresFact]
    public async Task PauseDuringBatch_AllowsCurrentSendReleasesTailAndResumesWithoutDuplicates()
    {
        var id = await CreateStartedCampaignAsync(4);
        await using var workerScope = _provider!.CreateAsyncScope();
        var processing = workerScope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        var campaigns = workerScope.ServiceProvider.GetRequiredService<ICampaignService>();
        var startedAt = (await campaigns.GetCampaignAsync(id, CancellationToken.None))!.StartedAt;
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
        foreach (var delivery in batch.Skip(1))
            await processing.ProcessAsync(delivery, CancellationToken.None);

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
        Assert.Equal(4, _sender.Requests.Select(request => request.DeliveryId).Distinct().Count());
    }

    [PostgresFact]
    public async Task StatusUpdates_CannotOverwriteConcurrentPauseOrCompletion()
    {
        var id = await CreateStartedCampaignAsync();
        await using var first = _provider!.CreateAsyncScope();
        await using var second = _provider!.CreateAsyncScope();
        var firstRepository = first.ServiceProvider.GetRequiredService<ICampaingRepository>();
        var secondRepository = second.ServiceProvider.GetRequiredService<ICampaingRepository>();
        var paused = (await firstRepository.Get(id, CancellationToken.None)).Single();
        var completed = (await secondRepository.Get(id, CancellationToken.None)).Single();
        paused.StatusFromInProgressToPaused(DateTime.UtcNow);
        completed.StatusFromInProgressToCompleted(DateTime.UtcNow);

        var results = await Task.WhenAll(
            firstRepository.TryUpdateStatusAsync(paused, CampaignStatus.InProgress, CancellationToken.None),
            secondRepository.TryUpdateStatusAsync(completed, CampaignStatus.InProgress, CancellationToken.None));

        Assert.Single(results, updated => updated);
        var current = (await firstRepository.Get(id, CancellationToken.None)).Single();
        Assert.Equal(results[0] ? CampaignStatus.Paused : CampaignStatus.Completed, current.Status);
        Assert.False(await firstRepository.TryUpdateStatusAsync(results[0] ? completed : paused,
            CampaignStatus.InProgress, CancellationToken.None));
    }

    [PostgresFact]
    public async Task ReleaseCapture_CannotReleaseNewLeaseEvenForTheSameWorker()
    {
        await CreateStartedCampaignAsync();
        await using var scope = _provider!.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDeliveryRepository>();
        var now = DateTime.UtcNow;
        var previous = Assert.Single(await repository.CaptureBatchAsync(1, "worker-1", TimeSpan.FromSeconds(10), now, CancellationToken.None));
        Assert.Empty(await repository.CaptureBatchAsync(1, "worker-2", TimeSpan.FromSeconds(10), now, CancellationToken.None));
        Assert.Empty(await repository.ReleaseExpiredLocksAsync(now.AddSeconds(9), CancellationToken.None));
        var later = now.AddSeconds(11);
        Assert.Single(await repository.ReleaseExpiredLocksAsync(later, CancellationToken.None));
        var current = Assert.Single(await repository.CaptureBatchAsync(1, "worker-1", TimeSpan.FromSeconds(90), later, CancellationToken.None));

        Assert.Null(await repository.ReleaseCapturedAsync(previous.Id, "worker-1", previous.LockExpiresAt!.Value, later, CancellationToken.None));
        var stored = Assert.Single(await repository.Get(current.Id, CancellationToken.None));
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
            using var draft = await client.PostAsJsonAsync("/api/campaigns", new { name = "Черновик", messageText = "Текст" });
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
