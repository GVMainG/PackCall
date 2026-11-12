using Microsoft.EntityFrameworkCore;
using PackCall.Core;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;

namespace PackCall.Infrastructure.Repositories;

// Здесь PostgreSQL координирует захват, возврат и фиксацию результата между несколькими воркерами.
public sealed class DeliveryRepository(AppDbContext dbContext, IDbContextFactory<AppDbContext> contextFactory) : IDeliveryRepository
{
    public async Task AddBatchAsync(IReadOnlyList<Delivery> deliveries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        if (deliveries.Count == 0)
            return;

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        // Новые экземпляры с теми же Id и FK исключают загрузку в контекст графа
        // Campaign/Contact и удержание уже сохранённых доставок в памяти запроса.
        var rows = deliveries.Select(delivery => Delivery.Create(
            delivery.CampaignId, delivery.ContactId, delivery.AddressSnapshot ?? throw new InvalidOperationException("New delivery requires an address snapshot."),
            delivery.CreatedAt, delivery.Id));
        foreach (var campaign in deliveries.GroupBy(d => d.CampaignId))
        {
            if (!await context.CampaignChannels.AnyAsync(c => c.CampaignId == campaign.Key && c.Channel == "email", ct))
                context.CampaignChannels.Add(new CampaignChannel { CampaignId = campaign.Key, Channel = "email" });
            context.CampaignContacts.AddRange(campaign.Select(d => d.ContactId).Distinct()
                .Select(id => new CampaignContact { CampaignId = campaign.Key, ContactId = id }));
        }
        await context.Deliveries.AddRangeAsync(rows, ct);
        await context.SaveChangesAsync(ct);
    }

    public Task<Delivery?> Get(Guid id, CancellationToken ct) =>
        dbContext.Deliveries.SingleOrDefaultAsync(delivery => delivery.Id == id, ct);

    public async Task<PageResult<Delivery>> GetPage(
        Guid campaignId,
        int pageNumber,
        int pageSize,
        CancellationToken ct)
    {
        var skip = Pagination.GetOffset(pageNumber, pageSize);
        var query = dbContext.Deliveries.AsNoTracking().Where(delivery => delivery.CampaignId == campaignId);

        var totalCount = await query.CountAsync(ct);
        var items = await query.OrderBy(delivery => delivery.Id).Skip(skip).Take(pageSize).ToListAsync(ct);

        return new PageResult<Delivery>(items, totalCount);
    }

    public async Task<DeliveryStatusCounts> GetStatusCountsAsync(Guid campaignId, CancellationToken ct)
    {
        var counts = await dbContext.Deliveries.AsNoTracking()
            .Where(delivery => delivery.CampaignId == campaignId)
            .GroupBy(delivery => delivery.DeliveryStatus)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        int Count(DeliveryStatus status) => counts.FirstOrDefault(item => item.Status == status)?.Count ?? 0;
        return new DeliveryStatusCounts(counts.Sum(item => item.Count),
            Count(DeliveryStatus.Waiting), Count(DeliveryStatus.InProgress),
            Count(DeliveryStatus.Sent), Count(DeliveryStatus.Failed));
    }

    public async Task<IReadOnlyList<Delivery>> CaptureBatchAsync(
        int batchSize,
        string workerId,
        TimeSpan lockDuration,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (lockDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockDuration));

        var waiting = (int)DeliveryStatus.Waiting;
        var inProgress = (int)DeliveryStatus.InProgress;
        var campaignInProgress = (int)CampaignStatus.InProgress;

        // SKIP LOCKED позволяет воркерам брать непересекающиеся пакеты без ожидания чужих блокировок.
        // Срок закрепления отсчитывается после блокировки всего пакета.
        return await dbContext.Deliveries
            .FromSqlInterpolated($$"""
                WITH candidates AS MATERIALIZED (
                    SELECT delivery.id
                    FROM deliveries AS delivery
                    INNER JOIN campaigns AS campaign ON campaign.id = delivery.campaign_id
                    WHERE delivery.delivery_status = {{waiting}}
                      AND campaign.status = {{campaignInProgress}}
                      AND delivery.channel = 'email'
                      AND delivery.address_snapshot IS NOT NULL
                      AND delivery.address_snapshot !~ '^[[:space:]]*$'
                    ORDER BY delivery.id
                    LIMIT {{batchSize}}
                    FOR UPDATE OF delivery SKIP LOCKED
                ),
                lease_time AS MATERIALIZED (
                    SELECT clock_timestamp() AS now FROM (SELECT count(*) FROM candidates) AS locked_batch
                ),
                updated AS (
                    UPDATE deliveries AS delivery
                    SET delivery_status = {{inProgress}},
                        worker_id = {{workerId}},
                        lock_expires_at = lease_time.now + {{lockDuration}},
                        updated_at = lease_time.now
                    FROM candidates CROSS JOIN lease_time
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
        CancellationToken ct)
    {
        var waiting = (int)DeliveryStatus.Waiting;
        var inProgress = (int)DeliveryStatus.InProgress;

        // При восстановлении проверяем срок повторно после блокировки строки.
        return await dbContext.Deliveries
            .FromSqlInterpolated($$"""
                WITH locked AS MATERIALIZED (
                    SELECT id, delivery_status, lock_expires_at FROM deliveries
                    WHERE delivery_status = {{inProgress}} AND lock_expires_at <= clock_timestamp()
                    ORDER BY id FOR UPDATE
                ),
                updated AS (
                    UPDATE deliveries AS delivery
                    SET delivery_status = {{waiting}},
                        worker_id = NULL,
                        lock_expires_at = NULL,
                        updated_at = clock_timestamp()
                    FROM locked
                    WHERE delivery.id = locked.id AND locked.delivery_status = {{inProgress}}
                      AND locked.lock_expires_at <= clock_timestamp()
                    RETURNING delivery.*
                )
                SELECT * FROM updated
                ORDER BY id
                """)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<Delivery?> ReleaseCapturedAsync(
        Guid deliveryId, string workerId, DateTime lockExpiresAt, DateTime now, CancellationToken ct)
    {
        var waiting = (int)DeliveryStatus.Waiting;
        var inProgress = (int)DeliveryStatus.InProgress;
        var released = await dbContext.Deliveries
            .FromSqlInterpolated($$"""
                UPDATE deliveries
                SET delivery_status = {{waiting}}, worker_id = NULL, lock_expires_at = NULL, updated_at = {{now}}
                WHERE id = {{deliveryId}} AND delivery_status = {{inProgress}}
                  AND worker_id = {{workerId}} AND lock_expires_at = {{lockExpiresAt}}
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(ct);
        return released.SingleOrDefault();
    }

    public async Task<bool> TryCompleteAsync(
        Delivery delivery, string? workerId, DateTime? lockExpiresAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        // Снимок Get() может отслеживаться EF. Отсоединяем до проверок и SQL, чтобы
        // после ошибки или отмены SaveChanges не сохранил отклонённый результат.
        dbContext.Entry(delivery).State = EntityState.Detached;
        ct.ThrowIfCancellationRequested();
        if (delivery.DeliveryStatus is not (DeliveryStatus.Sent or DeliveryStatus.Failed))
            throw new ArgumentException("Ожидается конечный результат доставки.", nameof(delivery));
        if (string.IsNullOrWhiteSpace(workerId) || lockExpiresAt is null)
            return false;

        var inProgress = (int)DeliveryStatus.InProgress;
        var terminal = (int)delivery.DeliveryStatus;
        // Срок читается из locked после FOR UPDATE; поле целевой таблицы позволило бы
        // проверить условие до блокировки. clock_timestamp(), в отличие от now(),
        // учитывает время ожидания блокировки.
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
            WITH locked AS MATERIALIZED (
                SELECT id, lock_expires_at FROM deliveries WHERE id = {{delivery.Id}} FOR UPDATE
            )
            UPDATE deliveries AS delivery
            SET delivery_status = {{terminal}}, updated_at = {{delivery.UpdatedAt}},
                completed_at = {{delivery.CompletedAt}}, worker_id = NULL, lock_expires_at = NULL
            FROM locked
            WHERE delivery.id = locked.id AND delivery.delivery_status = {{inProgress}}
              AND delivery.worker_id = {{workerId}} AND delivery.lock_expires_at = {{lockExpiresAt}}
              AND locked.lock_expires_at > clock_timestamp()
            """, ct);
        return affected == 1;
    }

    public Task<bool> HasPendingDeliveriesAsync(Guid campaignId, CancellationToken ct) =>
        dbContext.Deliveries.AnyAsync(
            delivery => delivery.CampaignId == campaignId &&
                (delivery.DeliveryStatus == DeliveryStatus.Waiting ||
                 delivery.DeliveryStatus == DeliveryStatus.InProgress),
            ct);
}
