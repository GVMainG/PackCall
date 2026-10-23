using PackCall.Core.Entities;
using PackCall.Core.Metrics;

namespace PackCall.Infrastructure.Metrics;

internal sealed class NoOpDeliveryMetrics : IDeliveryMetrics
{
    public void RecordCaptured(int count) { }

    public void RecordProcessed() { }

    public void RecordStatusChange(DeliveryStatus previous, DeliveryStatus current) { }

    public void RecordExpiredLockReturned(int count) { }

    public void RecordOperatorSuccess() { }

    public void RecordOperatorError() { }
}
