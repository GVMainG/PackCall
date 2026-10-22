using PackCall.Core.Entities;

namespace PackCall.Core.Test;

/// <summary>
/// Prepares persisted delivery snapshots for tests. Production capture and release
/// update these fields directly in SQL, then EF materializes the private setters.
/// </summary>
internal static class DeliveryTestData
{
    public static void SetCapturedForTest(this Delivery delivery, string workerId, DateTime lockExpiresAt, DateTime? now = null)
    {
        Set(delivery, nameof(Delivery.DeliveryStatus), DeliveryStatus.InProgress);
        Set(delivery, nameof(Delivery.WorkerId), workerId);
        Set(delivery, nameof(Delivery.LockExpiresAt), lockExpiresAt);
        Set(delivery, nameof(Delivery.UpdatedAt), now ?? DateTime.UtcNow);
    }

    public static void SetWaitingForTest(this Delivery delivery, DateTime now)
    {
        Set(delivery, nameof(Delivery.DeliveryStatus), DeliveryStatus.Waiting);
        Set(delivery, nameof(Delivery.WorkerId), null);
        Set(delivery, nameof(Delivery.LockExpiresAt), null);
        Set(delivery, nameof(Delivery.UpdatedAt), now);
    }

    private static void Set(Delivery delivery, string property, object? value) =>
        typeof(Delivery).GetProperty(property)!.SetValue(delivery, value);
}
