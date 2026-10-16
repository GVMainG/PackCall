using Microsoft.EntityFrameworkCore;
using PackCall.Core;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using System.Linq.Expressions;

namespace PackCall.Infrastructure.Repositories;

public sealed class DeliveryRepository(AppDbContext dbContext) : IDeliveryRepository
{
    public async Task<IEnumerable<Delivery>> Get(Guid id, CancellationToken ct) =>
        await dbContext.Deliveries
            .Include(delivery => delivery.Recipient)
            .Where(delivery => delivery.Id == id)
            .ToListAsync(ct);

    public async Task<IEnumerable<Delivery>> Get(
        Guid campaignId,
        Expression<Func<Delivery, bool>> predicate,
        CancellationToken ct) =>
        await dbContext.Deliveries
            .Include(delivery => delivery.Recipient)
            .Where(delivery => delivery.CampaignId == campaignId)
            .Where(predicate)
            .ToListAsync(ct);

    public async Task<PageResult<Delivery>> GetPage(
        Guid campaignId,
        int pageNumber,
        int pageSize,
        CancellationToken ct)
    {
        var skip = GetSkip(pageNumber, pageSize);
        var query = dbContext.Deliveries
            .Include(delivery => delivery.Recipient)
            .Where(delivery => delivery.CampaignId == campaignId)
            .OrderBy(delivery => delivery.Id);

        var totalCount = await query.CountAsync(ct);
        var items = await query.Skip(skip).Take(pageSize).ToListAsync(ct);

        return new PageResult<Delivery>(items, totalCount);
    }

    public async Task<IReadOnlyList<Delivery>> CaptureBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan lockDuration,
        DateTime now,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (lockDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockDuration));

        var lockExpiresAt = now + lockDuration;
        var waiting = (int)DeliveryStatus.Waiting;
        var inProgress = (int)DeliveryStatus.InProgress;
        var campaignInProgress = (int)CampaignStatus.InProgress;

        return await dbContext.Deliveries
            .FromSqlInterpolated($$"""
                WITH candidates AS (
                    SELECT delivery.id
                    FROM deliveries AS delivery
                    INNER JOIN campaigns AS campaign ON campaign.id = delivery.campaign_id
                    WHERE delivery.delivery_status = {{waiting}}
                      AND campaign.status = {{campaignInProgress}}
                    ORDER BY delivery.id
                    LIMIT {{batchSize}}
                    FOR UPDATE OF delivery SKIP LOCKED
                ),
                updated AS (
                    UPDATE deliveries AS delivery
                    SET delivery_status = {{inProgress}},
                        worker_id = {{workerId}},
                        lock_expires_at = {{lockExpiresAt}},
                        updated_at = {{now}}
                    FROM candidates
                    WHERE delivery.id = candidates.id
                    RETURNING delivery.*
                )
                SELECT * FROM updated
                ORDER BY id
                """)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Delivery>> ReleaseExpiredLocksAsync(
        DateTime now,
        CancellationToken ct)
    {
        var waiting = (int)DeliveryStatus.Waiting;
        var inProgress = (int)DeliveryStatus.InProgress;

        return await dbContext.Deliveries
            .FromSqlInterpolated($$"""
                WITH updated AS (
                    UPDATE deliveries AS delivery
                    SET delivery_status = {{waiting}},
                        worker_id = NULL,
                        lock_expires_at = NULL,
                        updated_at = {{now}}
                    WHERE delivery.delivery_status = {{inProgress}}
                      AND delivery.lock_expires_at <= {{now}}
                    RETURNING delivery.*
                )
                SELECT * FROM updated
                ORDER BY id
                """)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task SaveAsync(Delivery delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (dbContext.Entry(delivery).State == EntityState.Detached)
            dbContext.Deliveries.Update(delivery);

        await dbContext.SaveChangesAsync(ct);
    }

    public Task<bool> HasPendingDeliveriesAsync(Guid campaignId, CancellationToken ct) =>
        dbContext.Deliveries.AnyAsync(
            delivery => delivery.CampaignId == campaignId &&
                (delivery.DeliveryStatus == DeliveryStatus.Waiting ||
                 delivery.DeliveryStatus == DeliveryStatus.InProgress),
            ct);

    private static int GetSkip(int pageNumber, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        try
        {
            return checked((pageNumber - 1) * pageSize);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), exception);
        }
    }
}
