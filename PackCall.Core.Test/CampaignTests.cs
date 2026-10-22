using PackCall.Core.Entities;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Test;

public class CampaignTests
{
    [Fact]
    public void PauseAndResume_PreserveFirstStartAndMakeRepeatedRequestsHarmless()
    {
        var campaign = Campaign.Create("Название", "Текст");
        campaign.StatusFromDraftToInProgress();
        var startedAt = campaign.StartedAt;
        var pausedAt = DateTime.UtcNow;

        campaign.StatusFromInProgressToPaused(pausedAt);
        campaign.StatusFromInProgressToPaused(pausedAt.AddSeconds(1));
        Assert.Equal(CampaignStatus.Paused, campaign.Status);
        Assert.Equal(pausedAt, campaign.UpdatedAt);
        Assert.Throws<InvalidStatusTransitionException>(() => campaign.StatusFromDraftToInProgress());
        Assert.Throws<InvalidStatusTransitionException>(() => campaign.StatusFromInProgressToCompleted(DateTime.UtcNow));
        Assert.Throws<InvalidOperationException>(() => campaign.UpdateDetails("Другое имя", "Другой текст", DateTime.UtcNow));

        var resumedAt = pausedAt.AddSeconds(2);
        campaign.StatusFromPausedToInProgress(resumedAt);
        campaign.StatusFromPausedToInProgress(resumedAt.AddSeconds(1));
        Assert.Equal(CampaignStatus.InProgress, campaign.Status);
        Assert.Equal(resumedAt, campaign.UpdatedAt);
        Assert.Equal(startedAt, campaign.StartedAt);
        Assert.Null(campaign.CompletedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PauseAndResume_RejectDraftAndCompletedCampaigns(bool completed)
    {
        var campaign = Campaign.Create("Название", "Текст");
        if (completed)
        {
            campaign.StatusFromDraftToInProgress();
            campaign.StatusFromInProgressToCompleted(DateTime.UtcNow);
        }

        Assert.Throws<InvalidStatusTransitionException>(() => campaign.StatusFromInProgressToPaused(DateTime.UtcNow));
        Assert.Throws<InvalidStatusTransitionException>(() => campaign.StatusFromPausedToInProgress(DateTime.UtcNow));
        Assert.Equal(completed ? CampaignStatus.Completed : CampaignStatus.Draft, campaign.Status);
    }

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
