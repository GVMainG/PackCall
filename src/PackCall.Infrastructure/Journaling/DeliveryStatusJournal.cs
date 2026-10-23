using Microsoft.EntityFrameworkCore;
using PackCall.Core.Journaling;

namespace PackCall.Infrastructure.Journaling;

internal sealed class DeliveryStatusJournal(IDbContextFactory<AppDbContext> contextFactory)
    : IDeliveryStatusJournal
{
    public async Task AppendAsync(StatusChangeEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        context.DeliveryStatusJournal.Add(DeliveryStatusJournalRecord.FromEntry(entry));
        await context.SaveChangesAsync(ct);
    }
}
