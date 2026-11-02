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
using System.Text.Json;

namespace PackCall.Core.Test;

public abstract class PostgresTestBase : IAsyncLifetime
{
    private protected readonly string _schema = "packcall_pause_test_" + Guid.NewGuid().ToString("N");
    private protected readonly FakeMessageSender _sender = new();
    private protected ServiceProvider? _provider;
    private protected string _connectionString = "";
    private protected bool _schemaCreated;

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

    private protected void AddServices(IServiceCollection services)
    {
        services.AddInfrastructure(_connectionString, new PackCall.Infrastructure.Hosting.TestOperatorSettings(PackCall.Infrastructure.Hosting.TestSenderMode.Inprocess, false));
        services.AddSingleton<IMessageSender>(_sender);
        services.AddScoped<ICampaignService, CampaignService>();
        services.AddScoped<IDeliveryProcessingService, DeliveryProcessingService>();
    }

    private protected async Task<Guid> CreateStartedCampaignAsync(int recipients = 1)
    {
        await using var scope = _provider!.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        for (var i = 0; i < recipients; i++)
            await service.CreateRecipientAsync($"user{i}@example.com", CancellationToken.None);
        var id = await service.CreateCampaignAsync("Рассылка", "Сообщение", CancellationToken.None);
        await service.StartCampaignAsync(id, CancellationToken.None);
        return id;
    }

    private protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private protected static SendResult Result(bool success) => success ? SendResult.Success() : SendResult.Failure("declined");
    private protected async Task<Delivery> ReadDelivery(Guid id)
    {
        await using var scope = _provider!.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Deliveries.AsNoTracking().SingleAsync(d => d.Id == id);
    }
    private protected static string Snapshot(Delivery d) => JsonSerializer.Serialize(new { d.Id, d.CampaignId, d.RecipientId, d.RecipientEmail, d.CreatedAt, d.DeliveryStatus, d.UpdatedAt, d.CompletedAt, d.WorkerId, d.LockExpiresAt });
    private protected async Task<string> Journal(Guid id, NpgsqlConnection? observer = null)
    {
        await using var owned = observer is null ? new NpgsqlConnection(_connectionString) : null;
        var connection = observer ?? owned!;
        if (owned is not null) await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT coalesce(json_agg(j ORDER BY id)::text, '[]') FROM delivery_status_journal j WHERE delivery_id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }
    private protected async Task AssertTerminal(Delivery original, bool success)
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

    private protected async Task<DateTime> DatabaseNow(NpgsqlConnection? observer = null)
    {
        await using var owned = observer is null ? new NpgsqlConnection(_connectionString) : null;
        var connection = observer ?? owned!;
        if (owned is not null) await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        return (DateTime)(await query.ExecuteScalarAsync())!;
    }

    private protected async Task WaitBlocked(int pid, NpgsqlConnection? observer = null)
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

}
