using System.Diagnostics.Metrics;
using PackCall.Core.Entities;
using PackCall.Core.Metrics;

namespace PackCall.Infrastructure.Metrics;

public sealed class DeliveryMetrics : IDeliveryMetrics, IDisposable
{
    public const string MeterName = "PackCall.Deliveries";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _captured;
    private readonly Counter<long> _processed;
    private readonly Counter<long> _statusChanges;
    private readonly Counter<long> _expiredLocksReturned;
    private readonly Counter<long> _operatorCalls;
    private long _capturedTotal;
    private long _processedTotal;
    private long _expiredLocksReturnedTotal;
    private long _operatorSuccessTotal;
    private long _operatorErrorTotal;

    public DeliveryMetrics()
    {
        _captured = _meter.CreateCounter<long>(
            "packcall.deliveries.captured",
            unit: "{delivery}",
            description: "Number of deliveries captured by workers.");

        _processed = _meter.CreateCounter<long>(
            "packcall.deliveries.processed",
            unit: "{delivery}",
            description: "Number of deliveries that reached a terminal status.");

        _statusChanges = _meter.CreateCounter<long>(
            "packcall.deliveries.status_changes",
            unit: "{change}",
            description: "Number of delivery status transitions.");

        _expiredLocksReturned = _meter.CreateCounter<long>(
            "packcall.deliveries.expired_locks_returned",
            unit: "{delivery}",
            description: "Number of deliveries returned to Waiting after lock expiration.");

        _operatorCalls = _meter.CreateCounter<long>(
            "packcall.mock_email.calls",
            unit: "{call}",
            description: "Number of mock-email calls grouped by result.");
    }

    public void RecordCaptured(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > 0)
        {
            _captured.Add(count);
            Interlocked.Add(ref _capturedTotal, count);
        }
    }

    public void RecordProcessed()
    {
        _processed.Add(1);
        Interlocked.Increment(ref _processedTotal);
    }

    public void RecordStatusChange(DeliveryStatus previous, DeliveryStatus current) =>
        _statusChanges.Add(
            1,
            new KeyValuePair<string, object?>("previous_status", previous.ToString()),
            new KeyValuePair<string, object?>("new_status", current.ToString()));

    public void RecordExpiredLockReturned(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > 0)
        {
            _expiredLocksReturned.Add(count);
            Interlocked.Add(ref _expiredLocksReturnedTotal, count);
        }
    }

    public void RecordOperatorSuccess()
    {
        _operatorCalls.Add(1, new KeyValuePair<string, object?>("result", "success"));
        Interlocked.Increment(ref _operatorSuccessTotal);
    }

    public void RecordOperatorError()
    {
        _operatorCalls.Add(1, new KeyValuePair<string, object?>("result", "error"));
        Interlocked.Increment(ref _operatorErrorTotal);
    }

    public DeliveryMetricTotals GetTotals() => new(
        Interlocked.Read(ref _capturedTotal), Interlocked.Read(ref _processedTotal),
        Interlocked.Read(ref _expiredLocksReturnedTotal),
        Interlocked.Read(ref _operatorSuccessTotal), Interlocked.Read(ref _operatorErrorTotal));

    public void Dispose() => _meter.Dispose();
}
