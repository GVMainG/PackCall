using PackCall.Core.Entities;

namespace PackCall.Core.Test;

// Pins the dual-language numeric status contract (AD-5): C# enum int values
// must equal the integers the PostgreSQL schema and Python/SQL acceptance read.
[Trait("Category", "Unit")]
[Trait("Module", "Deliveries")]
public sealed class DeliveryStatusContractTests
{
    [Theory]
    [InlineData(DeliveryStatus.Waiting, 0)]
    [InlineData(DeliveryStatus.InProgress, 1)]
    [InlineData(DeliveryStatus.Sent, 2)]
    [InlineData(DeliveryStatus.Failed, -1)]
    public void DeliveryStatusIntValuesMatch(DeliveryStatus status, int value) =>
        Assert.Equal(value, (int)status);

    [Theory]
    [InlineData(CampaignStatus.Draft, 0)]
    [InlineData(CampaignStatus.InProgress, 1)]
    [InlineData(CampaignStatus.Completed, 2)]
    [InlineData(CampaignStatus.Forming, 3)]
    [InlineData(CampaignStatus.Paused, 4)]
    [InlineData(CampaignStatus.Failed, -1)]
    public void CampaignStatusIntValuesMatch(CampaignStatus status, int value) =>
        Assert.Equal(value, (int)status);
}
