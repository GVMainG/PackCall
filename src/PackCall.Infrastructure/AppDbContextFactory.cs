using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PackCall.Infrastructure;

internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PACKCALL_CONNECTION_STRING")
            ?? "Host=localhost;Database=packcall;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connectionString,
                pgOptions => pgOptions.MigrationsHistoryTable("__ef_migrations_history"))
            .Options;

        return new AppDbContext(options);
    }
}
