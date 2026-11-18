using Microsoft.EntityFrameworkCore;
using PackCall.Core;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;

namespace PackCall.Infrastructure.Repositories;

// Здесь PostgreSQL координирует захват, возврат и фиксацию результата между несколькими воркерами.
public sealed class DeliveryRepository(
    AppDbContext dbContext, IDbContextFactory<AppDbContext> contextFactory, IChannelOperatorAvailability operators) : IDeliveryRepository
{
    public async Task AddLegacyEmailBatchAsync(IReadOnlyList<Delivery> deliveries, CancellationToken ct)
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
                context.CampaignChannels.Add(new CampaignChannel(campaign.Key, "email"));
            context.CampaignContacts.AddRange(campaign.Select(d => d.ContactId).Distinct()
                .Select(id => new CampaignContact(campaign.Key, id)));
        }
        await context.Deliveries.AddRangeAsync(rows, ct);
        await context.SaveChangesAsync(ct);
    }

    public async Task<int> FormDeliveriesFromSnapshotAsync(Guid campaignId, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        // Кампания должна оставаться в Forming до фиксации доставок.
        var status = await context.Database.SqlQuery<int?>($"""
            SELECT status FROM campaigns WHERE id = {campaignId} FOR NO KEY UPDATE
            """).AsAsyncEnumerable().SingleOrDefaultAsync(ct);
        if (status is null)
            throw new KeyNotFoundException($"Кампания {campaignId} не найдена.");
        if (status != (int)CampaignStatus.Forming)
            throw new InvalidOperationException("Формирование доставок доступно только для кампании в статусе Forming.");

        var inserted = await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO deliveries (id, campaign_id, contact_id, channel, address_snapshot, delivery_status, created_at, updated_at)
            SELECT gen_random_uuid(), selected_contact.campaign_id, selected_contact.contact_id,
                   selected_channel.channel, address.address, {(int)DeliveryStatus.Waiting},
                   statement_timestamp(), statement_timestamp()
            FROM campaign_contacts AS selected_contact
            INNER JOIN contact_channels AS address ON address.contact_id = selected_contact.contact_id
            INNER JOIN campaign_channels AS selected_channel
                ON selected_channel.campaign_id = selected_contact.campaign_id
                AND selected_channel.channel = address.channel
            WHERE selected_contact.campaign_id = {campaignId}
            """, ct);

        await transaction.CommitAsync(ct);
        return inserted;
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

    public Task<DeliveryStatusCounts> GetStatusCountsAsync(Guid campaignId, CancellationToken ct) =>
        ReadStatusCountsAsync(dbContext, campaignId, ct);

    internal static async Task<DeliveryStatusCounts> ReadStatusCountsAsync(
        AppDbContext dbContext, Guid campaignId, CancellationToken ct)
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

        ct.ThrowIfCancellationRequested();
        var waiting = (int)DeliveryStatus.Waiting;
        var inProgress = (int)DeliveryStatus.InProgress;
        var campaignInProgress = (int)CampaignStatus.InProgress;
        var channels = ContactChannel.SupportedChannels.Where(operators.HasOperator).ToArray();
        if (channels.Length == 0)
            return [];

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
                      AND delivery.channel = ANY ({{channels}})
                      AND delivery.address_snapshot IS NOT NULL
                      AND btrim(delivery.address_snapshot, U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000') <> ''
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
