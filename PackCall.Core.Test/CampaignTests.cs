using PackCall.Core.Entities;
using PackCall.Core.Exceptions;

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

        var error = Assert.Throws<InvalidStatusTransitionException>(() => campaign.StatusFromDraftToInProgress());

        Assert.Equal(nameof(Campaign), error.EntityName);
        Assert.Equal(CampaignStatus.Completed, error.PreviousStatus);
        Assert.Equal(CampaignStatus.InProgress, error.TargetStatus);
        Assert.Contains("переходы из этого статуса запрещены", error.Message);
        Assert.Equal(CampaignStatus.Completed, campaign.Status);
        Assert.Equal(Now, campaign.CompletedAt);
        Assert.Equal(Now, campaign.UpdatedAt);
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

        var updatedAt = campaign.UpdatedAt;

        var error = Assert.Throws<InvalidStatusTransitionException>(() => campaign.StatusFromInProgressToCompleted(Now));

        Assert.Equal(nameof(Campaign), error.EntityName);
        Assert.Equal(CampaignStatus.Draft, error.PreviousStatus);
        Assert.Equal(CampaignStatus.Completed, error.TargetStatus);
        Assert.Contains("допустимые переходы:", error.Message);
        Assert.Contains("InProgress", error.Message);
        Assert.Contains("Forming", error.Message);
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.Null(campaign.CompletedAt);
        Assert.Equal(updatedAt, campaign.UpdatedAt);
    }

    [Theory]
    [InlineData(CampaignStatus.Draft, false)]
    [InlineData(CampaignStatus.Forming, false)]
    [InlineData(CampaignStatus.InProgress, false)]
    [InlineData(CampaignStatus.Completed, true)]
    [InlineData(CampaignStatus.Failed, true)]
    public void StatusTransitions_RecognizesTerminalStates(CampaignStatus status, bool expected)
    {
        Assert.Equal(expected, CampaignStatusTransitions.IsTerminal(status));
    }

    [Fact]
    public void StatusTransitions_AllowsOnlyStartAndCompletion()
    {
        foreach (var from in Enum.GetValues<CampaignStatus>())
        {
            foreach (var to in Enum.GetValues<CampaignStatus>())
            {
                var expected = (from, to) is
                    (CampaignStatus.Draft, CampaignStatus.Forming) or
                    (CampaignStatus.Forming, CampaignStatus.Draft) or
                    (CampaignStatus.Draft, CampaignStatus.InProgress) or
                    (CampaignStatus.InProgress, CampaignStatus.Completed);

                Assert.Equal(expected, CampaignStatusTransitions.CanTransition(from, to));
                Assert.Equal(expected, CampaignStatusTransitions.GetAllowedTargets(from).Contains(to));
            }
        }
    }
}
