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
    private readonly Counter<long> _operatorRetries;
    private readonly Counter<long> _operatorDuplicatesSuppressed;
    private readonly Counter<long> _operatorCalls;

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

        _operatorRetries = _meter.CreateCounter<long>(
            "packcall.mock_email.retries",
            unit: "{call}",
            description: "Number of repeated mock-email calls with an existing idempotency key.");

        _operatorDuplicatesSuppressed = _meter.CreateCounter<long>(
            "packcall.mock_email.duplicates_suppressed",
            unit: "{call}",
            description: "Number of duplicate mock-email sends suppressed by idempotency.");

        _operatorCalls = _meter.CreateCounter<long>(
            "packcall.mock_email.calls",
            unit: "{call}",
            description: "Number of mock-email calls grouped by result.");
    }

    public void RecordCaptured(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > 0)
            _captured.Add(count);
    }

    public void RecordProcessed() => _processed.Add(1);

    public void RecordStatusChange(DeliveryStatus previous, DeliveryStatus current) =>
        _statusChanges.Add(
            1,
            new KeyValuePair<string, object?>("previous_status", previous.ToString()),
            new KeyValuePair<string, object?>("new_status", current.ToString()));

    public void RecordExpiredLockReturned(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > 0)
            _expiredLocksReturned.Add(count);
    }

    public void RecordOperatorRetry() => _operatorRetries.Add(1);

    public void RecordOperatorDuplicateSuppressed() => _operatorDuplicatesSuppressed.Add(1);

    public void RecordOperatorSuccess() =>
        _operatorCalls.Add(1, new KeyValuePair<string, object?>("result", "success"));

    public void RecordOperatorError() =>
        _operatorCalls.Add(1, new KeyValuePair<string, object?>("result", "error"));

    public void Dispose() => _meter.Dispose();
}
