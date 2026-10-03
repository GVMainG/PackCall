using PackCall.Core.Entities;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Test;

public class DeliveryTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static Delivery CreateWaiting() =>
        Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "user@example.com", Now);

    [Fact]
    public void Create_SetsWaitingStatusAndEmailSnapshot()
    {
        var campaignId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();

        var delivery = Delivery.Create(campaignId, recipientId, "user@example.com", Now);

        Assert.NotEqual(Guid.Empty, delivery.Id);
        Assert.Equal(campaignId, delivery.CampaignId);
        Assert.Equal(recipientId, delivery.RecipientId);
        Assert.Equal("user@example.com", delivery.RecipientEmail);
        Assert.Equal(DeliveryStatus.Waiting, delivery.DeliveryStatus);
        Assert.Equal(Now, delivery.CreatedAt);
        Assert.Equal(Now, delivery.UpdatedAt);
        Assert.Null(delivery.CompletedAt);
        Assert.Null(delivery.WorkerId);
        Assert.Null(delivery.LockExpiresAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankEmail_Throws(string? email) =>
        Assert.ThrowsAny<ArgumentException>(() => Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), email!, Now));

    [Fact]
    public void Capture_FromWaiting_SetsInProgressAndLock()
    {
        var delivery = CreateWaiting();
        var lockExpiresAt = Now.AddMinutes(2);

        delivery.StatusFromWaitingToInProgress("worker-1", lockExpiresAt);

        Assert.Equal(DeliveryStatus.InProgress, delivery.DeliveryStatus);
        Assert.Equal("worker-1", delivery.WorkerId);
        Assert.Equal(lockExpiresAt, delivery.LockExpiresAt);
    }

    [Fact]
    public void Capture_WhenAlreadyInProgress_ThrowsInvalidTransition()
    {
        var delivery = CreateWaiting();
        delivery.StatusFromWaitingToInProgress("worker-1", Now.AddMinutes(2));

        Assert.Throws<InvalidStatusTransitionException>(() => delivery.StatusFromWaitingToInProgress("worker-2", Now.AddMinutes(4)));
    }

    [Fact]
    public void MarkSent_FromInProgress_SetsTerminalStatusAndClearsLock()
    {
        var delivery = CreateWaiting();
        delivery.StatusFromWaitingToInProgress("worker-1", Now.AddMinutes(2));
        var completedAt = Now.AddMinutes(1);

        delivery.StatusFromInProgressToSent(completedAt);

        Assert.Equal(DeliveryStatus.Sent, delivery.DeliveryStatus);
        Assert.Equal(completedAt, delivery.CompletedAt);
        Assert.Equal(completedAt, delivery.UpdatedAt);
        Assert.Null(delivery.WorkerId);
        Assert.Null(delivery.LockExpiresAt);
    }

    [Fact]
    public void MarkFailed_FromInProgress_SetsFailedStatus()
    {
        var delivery = CreateWaiting();
        delivery.StatusFromWaitingToInProgress("worker-1", Now.AddMinutes(2));

        delivery.StatusFromInProgressToFailed(Now.AddMinutes(1));

        Assert.Equal(DeliveryStatus.Failed, delivery.DeliveryStatus);
        Assert.NotNull(delivery.CompletedAt);
        Assert.Null(delivery.WorkerId);
        Assert.Null(delivery.LockExpiresAt);
    }

    [Fact]
    public void MarkSent_FromWaiting_ThrowsInvalidTransition()
    {
        var delivery = CreateWaiting();

        Assert.Throws<InvalidStatusTransitionException>(() => delivery.StatusFromInProgressToSent(Now));
    }

    [Fact]
    public void MarkSent_WhenAlreadySent_ThrowsInvalidTransition()
    {
        var delivery = CreateWaiting();
        delivery.StatusFromWaitingToInProgress("worker-1", Now.AddMinutes(2));
        delivery.StatusFromInProgressToSent(Now.AddMinutes(1));

        Assert.Throws<InvalidStatusTransitionException>(() => delivery.StatusFromInProgressToSent(Now.AddMinutes(2)));
    }

    [Fact]
    public void TryReturnIfLockExpired_WhenExpired_ReturnsToWaitingAndClearsLock()
    {
        var delivery = CreateWaiting();
        delivery.StatusFromWaitingToInProgress("worker-1", Now.AddMinutes(2));

        var returned = delivery.StatusFromInProgressToWaiting(Now.AddMinutes(3));

        Assert.True(returned);
        Assert.Equal(DeliveryStatus.Waiting, delivery.DeliveryStatus);
        Assert.Null(delivery.WorkerId);
        Assert.Null(delivery.LockExpiresAt);
        Assert.Equal(Now.AddMinutes(3), delivery.UpdatedAt);
    }

    [Fact]
    public void TryReturnIfLockExpired_WhenNotExpired_ReturnsFalse()
    {
        var delivery = CreateWaiting();
        delivery.StatusFromWaitingToInProgress("worker-1", Now.AddMinutes(2));

        var returned = delivery.StatusFromInProgressToWaiting(Now.AddMinutes(1));

        Assert.False(returned);
        Assert.Equal(DeliveryStatus.InProgress, delivery.DeliveryStatus);
        Assert.Equal("worker-1", delivery.WorkerId);
    }

    [Fact]
    public void Transitions_OnlySentAndFailedAreTerminal()
    {
        Assert.True(DeliveryStatusTransitions.IsTerminal(DeliveryStatus.Sent));
        Assert.True(DeliveryStatusTransitions.IsTerminal(DeliveryStatus.Failed));
        Assert.False(DeliveryStatusTransitions.IsTerminal(DeliveryStatus.Waiting));
        Assert.False(DeliveryStatusTransitions.IsTerminal(DeliveryStatus.InProgress));
    }
}
