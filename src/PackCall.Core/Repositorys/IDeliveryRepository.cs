using PackCall.Core.Entities;
using System.Linq.Expressions;

namespace PackCall.Core.Repositorys
{
    public interface IDeliveryRepository
    {
        /// <summary>Сохраняет порцию новых доставок без накопления отслеживаемых сущностей.</summary>
        Task AddBatchAsync(IReadOnlyList<Delivery> deliveries, CancellationToken ct);
        Task<IEnumerable<Delivery>> Get(Guid id, CancellationToken ct);
        /// <summary>
        /// Фильтрует доставки указанной кампании в хранилище, загружая также Recipient.
        /// Выражение должно допускать перевод в SQL.
        /// </summary>
        Task<IEnumerable<Delivery>> Get(Guid campaignId, Expression<Func<Delivery, bool>> predicate, CancellationToken ct);
        /// <summary>Возвращает страницу в порядке Id и общее число доставок кампании; нумерация с 1.</summary>
        Task<PageResult<Delivery>> GetPage(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct);
        /// <summary>Подсчитывает доставки кампании по статусам в хранилище.</summary>
        Task<DeliveryStatusCounts> GetStatusCountsAsync(Guid campaignId, CancellationToken ct);

        /// <summary>
        /// Атомарно выбирает не более <paramref name="batchSize"/> доставок со статусом "Ожидает"
        /// только у запущенных кампаний,
        /// переводит их в "Отправляется", записывает идентификатор воркера и срок закрепления
        /// и возвращает только успешно захваченные доставки.
        /// </summary>
        Task<IReadOnlyList<Delivery>> CaptureBatchAsync(
            int batchSize, string workerId, TimeSpan lockDuration, DateTime now, CancellationToken ct);

        /// <summary>
        /// Атомарно возвращает доставки со статусом "Отправляется" и истёкшим сроком закрепления
        /// в статус "Ожидает", очищая идентификатор воркера и срок закрепления.
        /// Возвращает возвращённые доставки.
        /// </summary>
        Task<IReadOnlyList<Delivery>> ReleaseExpiredLocksAsync(DateTime now, CancellationToken ct);

        /// <summary>Возвращает неотправленную доставку в Waiting только при совпадении владельца и срока закрепления.</summary>
        /// <returns>Освобождённая доставка или null, если закрепление уже изменилось.</returns>
        Task<Delivery?> ReleaseCapturedAsync(
            Guid deliveryId, string workerId, DateTime lockExpiresAt, DateTime now, CancellationToken ct);

        /// <summary>
        /// Сохраняет изменения доставки (смену статуса и служебных полей).
        /// </summary>
        Task SaveAsync(Delivery delivery, CancellationToken ct);

        /// <summary>
        /// Проверяет, есть ли у кампании доставки в статусах "Ожидает" или "Отправляется".
        /// </summary>
        Task<bool> HasPendingDeliveriesAsync(Guid campaignId, CancellationToken ct);

    }
}
