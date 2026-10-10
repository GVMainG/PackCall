using PackCall.Core.Entities;
using PackCall.Core.Journaling;

namespace PackCall.Infrastructure.Journaling;

internal sealed class DeliveryStatusJournalRecord
{
    private DeliveryStatusJournalRecord()
    {
    }

    public long Id { get; private set; }
    public Guid CampaignId { get; private set; }
    public Guid DeliveryId { get; private set; }
    public DeliveryStatus PreviousStatus { get; private set; }
    public DeliveryStatus NewStatus { get; private set; }
    public string? WorkerId { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public string? OperatorResult { get; private set; }

    public static DeliveryStatusJournalRecord FromEntry(StatusChangeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new DeliveryStatusJournalRecord
        {
            CampaignId = entry.CampaignId,
            DeliveryId = entry.DeliveryId,
            PreviousStatus = entry.PreviousStatus,
            NewStatus = entry.NewStatus,
            WorkerId = entry.WorkerId,
            OccurredAt = entry.OccurredAt,
            OperatorResult = entry.OperatorResult
        };
    }
}
