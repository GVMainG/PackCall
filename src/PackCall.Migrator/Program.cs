using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;

try
{
    var builder = Host.CreateApplicationBuilder(args);
    PackCallHosting.LoadConfiguration(builder.Configuration, builder.Environment.EnvironmentName, args);
    builder.Services.AddPackCallLogging(builder.Configuration, "PackCall.Migrator");
    builder.Services.AddStorage(PackCallHosting.ConnectionString(builder.Configuration));
    using var host = builder.Build();
    await host.StartAsync();
    try
    {
        var ct = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        ct.ThrowIfCancellationRequested();
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        ct.ThrowIfCancellationRequested();
        return 0;
    }
    finally { await host.StopAsync(); }
}
catch (Exception exception)
{
    // Provider exceptions can contain connection details; report a safe startup failure.
    Console.Error.WriteLine($"Migrator failed or cancelled ({exception.GetType().Name}).");
    return 1;
}
