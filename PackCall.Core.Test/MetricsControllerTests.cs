using Microsoft.Extensions.Logging.Abstractions;
using PackCall.Api.Controllers;
using PackCall.Core.Metrics;
using PackCall.Infrastructure.Metrics;

namespace PackCall.Core.Test;

public class MetricsControllerTests
{
    [Fact]
    public async Task Metrics_ExposeEventCountersAndDatabaseGauges()
    {
        using var metrics = new DeliveryMetrics();
        metrics.RecordCaptured(2);
        metrics.RecordProcessed();
        metrics.RecordExpiredLockReturned(1);
        metrics.RecordOperatorRetry();
        metrics.RecordOperatorDuplicateSuppressed();
        metrics.RecordOperatorSuccess();
        metrics.RecordOperatorError();

        var controller = new MetricsController(metrics,
            new SnapshotReader(new DeliveryStateSnapshot(3, 2, 5, 1, 2, 0)),
            NullLogger<MetricsController>.Instance);
        var result = await controller.GetMetrics(CancellationToken.None);

        Assert.Contains("text/plain; version=0.0.4", result.ContentType);
        Assert.Contains("packcall_deliveries_captured_total 2\n", result.Content);
        Assert.Contains("packcall_deliveries_processed_total 1\n", result.Content);
        Assert.Contains("packcall_deliveries{status=\"waiting\"} 3\n", result.Content);
        Assert.Contains("packcall_deliveries_active_locks 2\n", result.Content);
        Assert.Contains("packcall_deliveries_expired_locks_returned_total 1\n", result.Content);
        Assert.Contains("packcall_mock_email_calls_total{result=\"success\"} 1\n", result.Content);
        Assert.Contains("packcall_mock_email_calls_total{result=\"error\"} 1\n", result.Content);
        Assert.Contains("packcall_storage_up 1\n", result.Content);
    }

    [Fact]
    public async Task Metrics_KeepCountersAvailableWhenDatabaseIsUnavailable()
    {
        using var metrics = new DeliveryMetrics();
        metrics.RecordProcessed();
        var controller = new MetricsController(metrics, new FailingSnapshotReader(),
            NullLogger<MetricsController>.Instance);

        var result = await controller.GetMetrics(CancellationToken.None);

        Assert.Contains("packcall_deliveries_processed_total 1\n", result.Content);
        Assert.Contains("packcall_storage_up 0\n", result.Content);
        Assert.DoesNotContain("packcall_deliveries{status=", result.Content);
    }

    private sealed class SnapshotReader(DeliveryStateSnapshot snapshot) : IDeliveryStateSnapshotReader
    {
        public Task<DeliveryStateSnapshot> ReadAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private sealed class FailingSnapshotReader : IDeliveryStateSnapshotReader
    {
        public Task<DeliveryStateSnapshot> ReadAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Database unavailable");
    }
}
