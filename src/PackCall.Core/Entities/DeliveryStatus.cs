using System.Collections.ObjectModel;

namespace PackCall.Core.Entities;

public enum DeliveryStatus
{
    Failed = -1,
    Waiting = 0,
    InProgress = 1,
    /// <summary>Провайдер подтвердил приём сообщения.</summary>
    Sent = 2
}

/// <summary>
/// Допустимые переходы статусов доставки.
/// </summary>
public static class DeliveryStatusTransitions
{
    private static readonly ReadOnlySet<DeliveryStatus> WaitingTargets = [DeliveryStatus.InProgress];
    private static readonly ReadOnlySet<DeliveryStatus> InProgressTargets =
        [DeliveryStatus.Sent, DeliveryStatus.Failed, DeliveryStatus.Waiting];

    public static bool CanTransition(DeliveryStatus from, DeliveryStatus to) =>
        GetAllowedTargets(from).Contains(to);

    public static IReadOnlySet<DeliveryStatus> GetAllowedTargets(DeliveryStatus from) => from switch
    {
        DeliveryStatus.Waiting => WaitingTargets,
        DeliveryStatus.InProgress => InProgressTargets,
        _ => ReadOnlySet<DeliveryStatus>.Empty
    };
}
