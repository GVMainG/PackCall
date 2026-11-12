using System.Runtime.CompilerServices;
using PackCall.Core.Entities;

namespace PackCall.Core.Diagnostics;

/// <summary>Временные слабые ссылки не хранятся в сущностях и не экспортируются как идентификаторы.</summary>
public static class CaptureTiming
{
    private sealed record Stamp(TimeProvider Time, long Timestamp);
    private static readonly ConditionalWeakTable<Delivery, Stamp> Times = new();

    public static void Record(IReadOnlyList<Delivery> deliveries, TimeProvider time)
    {
        if (!PerformanceMetrics.Enabled) return;
        var stamp = new Stamp(time, time.GetTimestamp());
        foreach (var delivery in deliveries)
        {
            Times.Remove(delivery);
            Times.Add(delivery, stamp);
        }
    }

    public static double? TakeWaitMilliseconds(Delivery delivery)
    {
        if (!Times.TryGetValue(delivery, out var stamp)) return null;
        Times.Remove(delivery);
        return stamp.Time.GetElapsedTime(stamp.Timestamp).TotalMilliseconds;
    }
}
