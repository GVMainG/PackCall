using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace PackCall.Infrastructure.Hosting;

/// <summary>Проверяет схему без записи в БД после инициализации ConsoleLifetime и до запуска служб хоста.</summary>
public sealed class SchemaGate(IDbContextFactory<AppDbContext> factory, IHostApplicationLifetime lifetime,
    ILogger logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await using var db = await factory.CreateDbContextAsync(ct);
                    var known = db.Database.GetMigrations().Order().ToArray();
                    if (known.Length == 0)
                        throw new InvalidOperationException("Нет известных миграций схемы.");
                    var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).Order().ToArray();
                    // Отстающую схему можно дождаться, а неизвестная миграция означает несовместимость с этой сборкой.
                    if (applied.Except(known).Any())
                        throw new InvalidOperationException("Схема содержит неизвестные миграции.");
                    if (applied.SequenceEqual(known))
                    {
                        logger.Information("Schema ready. Version={SchemaVersion}", known[^1]);
                        return;
                    }
                    logger.Information("Ожидание схемы. Applied={Applied}, Expected={Expected}", applied, known);
                }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or TimeoutException)
                {
                    // Не пишем детали ошибки БД в журнал, поскольку параметры подключения могут содержать секреты.
                    logger.Warning("Ожидание доступности БД для проверки схемы");
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch
        {
            logger.Error("Schema gate failed or cancelled; application will not start");
            throw;
        }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
}
