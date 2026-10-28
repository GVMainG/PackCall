using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace PackCall.Infrastructure.Hosting;

public static class PackCallHosting
{
    public static void LoadConfiguration(ConfigurationManager configuration, string environment, string[] args)
    {
        configuration.Sources.Clear();
        configuration.SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.packcall.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("DOTNET_").AddEnvironmentVariables("ASPNETCORE_")
            .AddEnvironmentVariables().AddCommandLine(args);
    }

    public static string ConnectionString(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("ConnectionStrings:Postgres must be configured.");
        return value;
    }

    public static IServiceCollection AddPackCallLogging(this IServiceCollection services, IConfiguration configuration,
        string application, string? workerId = null)
    {
        services.AddSerilog((_, logger) => logger.ReadFrom.Configuration(configuration)
            .Enrich.WithProperty("Application", application).Enrich.WithProperty("WorkerId", workerId));
        return services;
    }

    public static IServiceCollection AddSchemaGate(this IServiceCollection services)
    {
        services.Configure<HostOptions>(options => options.ServicesStartConcurrently = false);
        services.AddHostedService<SchemaGate>();
        return services;
    }
}
