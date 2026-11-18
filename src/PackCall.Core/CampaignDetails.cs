using PackCall.Core.Entities;

namespace PackCall.Core;

public sealed record CampaignDetails(Campaign Campaign, DeliveryStatusCounts Deliveries,
    long SelectedContactCount, IReadOnlyList<string> Channels)
{
    public long? SkippedDeliveryCount => Campaign.Status == CampaignStatus.Forming
        ? null
        : SelectedContactCount * Channels.Count - Deliveries.Total;
}
