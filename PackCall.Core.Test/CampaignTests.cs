using PackCall.Core.Entities;

namespace PackCall.Core.Test;

public class CampaignTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static Campaign CreateDraft() => Campaign.Create("Название", "Текст сообщения");

    [Fact]
    public void Create_SetsDraftStateAndDates()
    {
        var campaign = Campaign.Create("Название", "Текст");

        Assert.NotEqual(Guid.Empty, campaign.Id);
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.NotNull(campaign.CreatedAt);
        Assert.NotNull(campaign.UpdatedAt);
        Assert.Null(campaign.StartedAt);
        Assert.Null(campaign.CompletedAt);
    }

    [Fact]
    public void ToInProgress_FromDraft_SetsStatusAndStartedAt()
    {
        var campaign = CreateDraft();

        campaign.StatusFromDraftToInProgress();

        Assert.Equal(CampaignStatus.InProgress, campaign.Status);
        Assert.NotNull(campaign.StartedAt);
        Assert.NotNull(campaign.UpdatedAt);
    }

    [Fact]
    public void ToInProgress_WhenAlreadyInProgress_DoesNothing()
    {
        var campaign = CreateDraft();
        campaign.StatusFromDraftToInProgress();
        var startedAt = campaign.StartedAt;

        campaign.StatusFromDraftToInProgress();

        Assert.Equal(CampaignStatus.InProgress, campaign.Status);
        Assert.Equal(startedAt, campaign.StartedAt);
    }

    [Fact]
    public void ToInProgress_FromCompleted_Throws()
    {
        var campaign = CreateDraft();
        campaign.StatusFromDraftToInProgress();
        campaign.StatusFromInProgressToCompleted(Now);

        Assert.Throws<InvalidOperationException>(() => campaign.StatusFromDraftToInProgress());
    }

    [Fact]
    public void UpdateDetails_OnInProgress_Throws()
    {
        var campaign = CreateDraft();
        campaign.StatusFromDraftToInProgress();

        Assert.Throws<InvalidOperationException>(() => campaign.UpdateDetails("Имя", "Текст", Now));
    }

    [Fact]
    public void Complete_FromInProgress_SetsCompletedAt()
    {
        var campaign = CreateDraft();
        campaign.StatusFromDraftToInProgress();

        campaign.StatusFromInProgressToCompleted(Now);

        Assert.Equal(CampaignStatus.Completed, campaign.Status);
        Assert.Equal(Now, campaign.CompletedAt);
        Assert.Equal(Now, campaign.UpdatedAt);
    }

    [Fact]
    public void Complete_WhenAlreadyCompleted_DoesNothing()
    {
        var campaign = CreateDraft();
        campaign.StatusFromDraftToInProgress();
        campaign.StatusFromInProgressToCompleted(Now);

        campaign.StatusFromInProgressToCompleted(Now.AddMinutes(1));

        Assert.Equal(Now, campaign.CompletedAt);
    }

    [Fact]
    public void Complete_FromDraft_Throws()
    {
        var campaign = CreateDraft();

        Assert.Throws<InvalidOperationException>(() => campaign.StatusFromInProgressToCompleted(Now));
    }
}
