using Microsoft.EntityFrameworkCore;
using PackCall.Core.Entities;
using PackCall.Core.Metrics;

namespace PackCall.Infrastructure.Metrics;

public sealed class DeliveryStateSnapshotReader(AppDbContext dbContext) : IDeliveryStateSnapshotReader
{
    public async Task<DeliveryStateSnapshot> ReadAsync(CancellationToken ct)
    {
        var counts = await dbContext.Deliveries.AsNoTracking()
            .GroupBy(delivery => delivery.DeliveryStatus)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var activeLocks = await dbContext.Deliveries.AsNoTracking()
            .CountAsync(delivery => delivery.DeliveryStatus == DeliveryStatus.InProgress &&
                delivery.LockExpiresAt > now, ct);
        var expiredLocks = await dbContext.Deliveries.AsNoTracking()
            .CountAsync(delivery => delivery.DeliveryStatus == DeliveryStatus.InProgress &&
                delivery.LockExpiresAt <= now, ct);

        int Count(DeliveryStatus status) => counts.FirstOrDefault(item => item.Status == status)?.Count ?? 0;
        return new DeliveryStateSnapshot(
            Count(DeliveryStatus.Waiting), Count(DeliveryStatus.InProgress),
            Count(DeliveryStatus.Sent), Count(DeliveryStatus.Failed),
            activeLocks, expiredLocks);
    }
}
