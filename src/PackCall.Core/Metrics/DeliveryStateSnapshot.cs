namespace PackCall.Core.Metrics;

/// <summary>Текущее состояние доставок во всех кампаниях.</summary>
public sealed record DeliveryStateSnapshot(
    int Waiting,
    int InProgress,
    int Sent,
    int Failed,
    int ActiveLocks,
    int ExpiredLocks);
