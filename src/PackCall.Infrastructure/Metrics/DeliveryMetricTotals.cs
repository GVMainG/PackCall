namespace PackCall.Infrastructure.Metrics;

/// <summary>Накопительные счётчики текущего процесса API.</summary>
public sealed record DeliveryMetricTotals(
    long Captured,
    long Processed,
    long ExpiredLocksReturned,
    long OperatorRetries,
    long OperatorDuplicatesSuppressed,
    long OperatorSuccess,
    long OperatorError);
