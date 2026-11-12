using Microsoft.EntityFrameworkCore;
using PackCall.Core.Journaling;

namespace PackCall.Infrastructure.Journaling;

internal sealed class DeliveryStatusJournal(IDbContextFactory<AppDbContext> contextFactory)
    : IDeliveryStatusJournal
{
    public async Task AppendAsync(StatusChangeEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Отдельный контекст сохраняет запись журнала без накопленных изменений репозитория.
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        context.DeliveryStatusJournal.Add(DeliveryStatusJournalRecord.FromEntry(entry));
        await context.SaveChangesAsync(ct);
    }
}
