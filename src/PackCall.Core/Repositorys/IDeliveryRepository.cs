using PackCall.Core.Models;

namespace PackCall.Core.Repositorys
{
    public interface IDeliveryRepository
    {
        Task<IEnumerable<Delivery>> Get(Guid id, CancellationToken ct);
        Task<IEnumerable<Delivery>> Get(Guid campaignId, Func<Delivery, bool> predicate, CancellationToken ct);
        Task<PageResult<Delivery>> GetPage(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct);

        /// <summary>
        /// Атомарно выбирает не более <paramref name="batchSize"/> доставок со статусом «Ожидает»,
        /// переводит их в «Отправляется», записывает идентификатор воркера и срок закрепления
        /// и возвращает только успешно захваченные доставки.
        /// </summary>
        Task<IReadOnlyList<Delivery>> CaptureBatchAsync(
            int batchSize, string workerId, TimeSpan lockDuration, DateTime now, CancellationToken ct);

        /// <summary>
        /// Атомарно возвращает доставки со статусом «Отправляется» и истёкшим сроком закрепления
        /// в статус «Ожидает», очищая идентификатор воркера и срок закрепления.
        /// Возвращает возвращённые доставки.
        /// </summary>
        Task<IReadOnlyList<Delivery>> ReleaseExpiredLocksAsync(DateTime now, CancellationToken ct);

        /// <summary>Сохраняет изменения доставки (смену статуса и служебных полей).</summary>
        Task SaveAsync(Delivery delivery, CancellationToken ct);

        /// <summary>Проверяет, есть ли у кампании доставки в статусах «Ожидает» или «Отправляется».</summary>
        Task<bool> HasPendingDeliveriesAsync(Guid campaignId, CancellationToken ct);

    }
}
