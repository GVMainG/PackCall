using Npgsql;

namespace PackCall.Core.Test;

// Proves that the CI migration step actually landed in the target database: a
// migrator that exits 0 without applying would otherwise go unnoticed because
// every other PostgreSQL test builds its own throwaway schema.
[Trait("Category", "Integration")]
[Trait("Module", "Migrations")]
public sealed class MigrationAppliedTests
{
    [Fact]
    public async Task LatestMigrationIsAppliedToTheTargetDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("PACKCALL_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PACKCALL_TEST_POSTGRES must be set.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"", connection);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));

        Assert.Contains("20261028193126_InitialCreate", ids);
    }
}
