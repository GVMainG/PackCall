using PackCall.Core.Entities;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Services;

/// <summary>Сценарии фоновой обработки доставок для обработчика в API.</summary>
/// <remarks>
/// Все операции поддерживают отмену через CancellationToken и могут выбросить
/// OperationCanceledException. Ошибки инфраструктуры передаются вызывающему коду.
/// Атомарность захвата и сохранение результатов обеспечивают реализации репозиториев.
/// </remarks>
public interface IDeliveryProcessingService
{
    /// <summary>Атомарно захватывает ожидающие доставки запущенных кампаний и фиксирует захват в журнале и метриках.</summary>
    /// <returns>Не более batchSize доставок, закреплённых за указанным воркером.</returns>
    /// <remarks>Повторный вызов захватывает следующий доступный пакет, не ранее захваченные доставки.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Размер пакета или срок закрепления не положителен.</exception>
    /// <exception cref="ArgumentException">Идентификатор воркера не заполнен.</exception>
    Task<IReadOnlyList<Delivery>> CaptureBatchAsync(
        int batchSize, string workerId, TimeSpan lockDuration, CancellationToken ct);

    /// <summary>Обрабатывает одну доставку из захваченного пакета и проверяет завершение её кампании.</summary>
    /// <remarks>
    /// Подтверждение приёма провайдером переводит доставку в Sent, явная ошибка — в Failed.
    /// Повторная обработка конечной доставки запрещена.
    /// При исключении отправителя доставка остаётся закреплённой до восстановления по сроку.
    /// Вызывающий обработчик передаёт только доставку, захваченную им, до истечения закрепления.
    /// При паузе кампании не начинает отправку и освобождает доставку для последующего возобновления.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Доставка равна null.</exception>
    /// <exception cref="DomainException">Доставка не в обработке или кампания не запущена и не приостановлена.</exception>
    /// <exception cref="KeyNotFoundException">Кампания доставки не найдена.</exception>
    Task ProcessAsync(Delivery delivery, CancellationToken ct);

    /// <summary>Возвращает доставки с истёкшим закреплением в Waiting, фиксирует возврат в журнале и метриках.</summary>
    /// <returns>Количество возвращённых доставок; повторный вызов не затрагивает уже возвращённые доставки.</returns>
    Task<int> ReleaseExpiredLocksAsync(CancellationToken ct);

    /// <summary>Завершает запущенную кампанию, если у неё нет ожидающих или обрабатываемых доставок.</summary>
    /// <returns>True, если кампания завершена этим вызовом; иначе false.</returns>
    /// <remarks>
    /// Ошибочные доставки не препятствуют завершению. Повторная проверка завершённой кампании не меняет даты.
    /// Запущенная кампания без доставок также может быть завершена.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">Кампания не найдена.</exception>
    Task<bool> CompleteCampaignIfFinishedAsync(Guid campaignId, CancellationToken ct);
}
