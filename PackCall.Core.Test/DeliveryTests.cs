using PackCall.Core.Entities;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Test;

public class DeliveryTests
{
    [Fact]
    public void Capture_WhenAlreadyInProgress_RejectsSecondWorker()
    {
        var now = DateTime.UtcNow;
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "user@example.com", now);
        delivery.StatusFromWaitingToInProgress("worker-1", now.AddMinutes(2), now);

        Assert.Throws<InvalidStatusTransitionException>(() =>
            delivery.StatusFromWaitingToInProgress("worker-2", now.AddMinutes(4), now));

        Assert.Equal("worker-1", delivery.WorkerId);
        Assert.Equal(now.AddMinutes(2), delivery.LockExpiresAt);
    }

    [Fact]
    public void ReturnBeforeLockExpiry_DoesNotReleaseDelivery()
    {
        var now = DateTime.UtcNow;
        var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "user@example.com", now);
        delivery.StatusFromWaitingToInProgress("worker-1", now.AddMinutes(2), now);

        var released = delivery.StatusFromInProgressToWaiting(now.AddMinutes(1));

        Assert.False(released);
        Assert.Equal(DeliveryStatus.InProgress, delivery.DeliveryStatus);
        Assert.Equal("worker-1", delivery.WorkerId);
    }
}
