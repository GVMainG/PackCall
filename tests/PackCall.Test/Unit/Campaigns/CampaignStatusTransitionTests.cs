using PackCall.Core.Entities;

namespace PackCall.Core.Test;

[Trait("Category", "Unit")]
[Trait("Module", "Campaigns")]
public sealed class CampaignStatusTransitionTests
{
    private const CampaignStatus Unknown = (CampaignStatus)999;

    public static TheoryData<CampaignStatus, CampaignStatus[]> AllowedTargets => new()
    {
        { CampaignStatus.Draft, [CampaignStatus.Forming, CampaignStatus.InProgress] },
        { CampaignStatus.Forming, [CampaignStatus.Draft] },
        { CampaignStatus.InProgress, [CampaignStatus.Paused, CampaignStatus.Completed] },
        { CampaignStatus.Paused, [CampaignStatus.InProgress] },
        { CampaignStatus.Completed, [] },
        { CampaignStatus.Failed, [] },
        { Unknown, [] }
    };

    [Theory]
    [MemberData(nameof(AllowedTargets))]
    public void TransitionMatrixMatchesDomainContract(CampaignStatus from, CampaignStatus[] expectedTargets)
    {
        Assert.Equal(expectedTargets.Order(), CampaignStatusTransitions.GetAllowedTargets(from).Order());

        foreach (var to in Enum.GetValues<CampaignStatus>().Append(Unknown))
            Assert.Equal(expectedTargets.Contains(to), CampaignStatusTransitions.CanTransition(from, to));
    }
}
