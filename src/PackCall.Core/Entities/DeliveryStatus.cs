namespace PackCall.Core.Models;

public enum DeliveryStatus
{
    Failed = -1,
    Waiting = 0,
    InProgress = 1,
    Sent = 2
}

/// <summary>Допустимые переходы статусов доставки.</summary>
public static class DeliveryStatusTransitions
{
    private static readonly IReadOnlyDictionary<DeliveryStatus, IReadOnlySet<DeliveryStatus>> Allowed =
        new Dictionary<DeliveryStatus, IReadOnlySet<DeliveryStatus>>
        {
            [DeliveryStatus.Waiting] = new HashSet<DeliveryStatus> { DeliveryStatus.InProgress },
            [DeliveryStatus.InProgress] = new HashSet<DeliveryStatus>
            {
                DeliveryStatus.Sent,
                DeliveryStatus.Failed,
                DeliveryStatus.Waiting
            },
            [DeliveryStatus.Sent] = new HashSet<DeliveryStatus>(),
            [DeliveryStatus.Failed] = new HashSet<DeliveryStatus>()
        };

    public static bool CanTransition(DeliveryStatus from, DeliveryStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlySet<DeliveryStatus> GetAllowedTargets(DeliveryStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : new HashSet<DeliveryStatus>();

    public static bool IsTerminal(DeliveryStatus status) =>
        status is DeliveryStatus.Sent or DeliveryStatus.Failed;
}
