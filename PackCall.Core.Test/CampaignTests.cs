using PackCall.Core.Entities;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Test;

public class CampaignTests
{
    [Fact]
    public void UpdateDetails_WhenCampaignIsInProgress_Throws()
    {
        var campaign = Campaign.Create("Название", "Текст");
        campaign.StatusFromDraftToInProgress();

        Assert.Throws<InvalidOperationException>(() =>
            campaign.UpdateDetails("Новое название", "Новый текст", DateTime.UtcNow));
    }

    [Fact]
    public void Complete_FromDraft_ThrowsWithoutChangingStatus()
    {
        var campaign = Campaign.Create("Название", "Текст");

        Assert.Throws<InvalidStatusTransitionException>(() =>
            campaign.StatusFromInProgressToCompleted(DateTime.UtcNow));

        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.Null(campaign.CompletedAt);
    }
}
