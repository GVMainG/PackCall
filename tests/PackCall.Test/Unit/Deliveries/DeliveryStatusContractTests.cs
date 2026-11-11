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

    [Theory]
    [InlineData(DeliveryStatus.Waiting, DeliveryStatus.InProgress)]
    [InlineData(DeliveryStatus.InProgress, DeliveryStatus.Sent, DeliveryStatus.Failed, DeliveryStatus.Waiting)]
    [InlineData(DeliveryStatus.Sent)]
    [InlineData(DeliveryStatus.Failed)]
    [InlineData((DeliveryStatus)99)]
    public void DeliveryTransitionsMatchContract(DeliveryStatus from, params DeliveryStatus[] targets)
    {
        Assert.Equal(targets, DeliveryStatusTransitions.GetAllowedTargets(from).ToArray());
        foreach (var to in Enum.GetValues<DeliveryStatus>())
            Assert.Equal(targets.Contains(to), DeliveryStatusTransitions.CanTransition(from, to));
        Assert.False(DeliveryStatusTransitions.CanTransition(from, (DeliveryStatus)99));
    }

    [Theory]
    [InlineData(CampaignStatus.Draft, CampaignStatus.Forming, CampaignStatus.InProgress)]
    [InlineData(CampaignStatus.Forming, CampaignStatus.Draft)]
    [InlineData(CampaignStatus.InProgress, CampaignStatus.Completed, CampaignStatus.Paused)]
    [InlineData(CampaignStatus.Paused, CampaignStatus.InProgress)]
    [InlineData(CampaignStatus.Completed)]
    [InlineData(CampaignStatus.Failed)]
    [InlineData((CampaignStatus)99)]
    public void CampaignTransitionsMatchContract(CampaignStatus from, params CampaignStatus[] targets)
    {
        Assert.Equal(targets, CampaignStatusTransitions.GetAllowedTargets(from).ToArray());
        foreach (var to in Enum.GetValues<CampaignStatus>())
            Assert.Equal(targets.Contains(to), CampaignStatusTransitions.CanTransition(from, to));
        Assert.False(CampaignStatusTransitions.CanTransition(from, (CampaignStatus)99));
    }
}
