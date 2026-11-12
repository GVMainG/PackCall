using Microsoft.EntityFrameworkCore;
using PackCall.Core;

namespace PackCall.Infrastructure;

// Воркер сверяется с теми же часами PostgreSQL, по которым выдано закрепление.
public sealed class PostgresLeaseClock(IDbContextFactory<AppDbContext> factory) : ILeaseClock
{
    public async Task<DateTime> GetUtcNowAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Database.SqlQueryRaw<DateTime>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
    }
}
