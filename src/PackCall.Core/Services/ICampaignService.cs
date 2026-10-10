using PackCall.Core.Entities;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Services;

/// <summary>Сценарии управления кампаниями для API.</summary>
/// <remarks>
/// Все операции поддерживают отмену через CancellationToken и могут выбросить
/// OperationCanceledException. Ошибки инфраструктуры передаются вызывающему коду.
/// Возвращаемые сущности используются внутри приложения; API преобразует их в HTTP-модели.
/// Нумерация страниц начинается с 1, размер страницы должен быть положительным.
/// </remarks>
public interface ICampaignService
{
    /// <summary>Создаёт черновик и по одной ожидающей доставке на каждого уникального получателя аудитории.</summary>
    /// <returns>Идентификатор сохранённой кампании.</returns>
    /// <remarks>
    /// Кампания и доставки сохраняются совместно. Адрес получателя сохраняется как снимок.
    /// Каждый вызов создаёт новую кампанию; повторный запрос не дедуплицируется.
    /// Название и сообщение проверяются при запуске, поэтому возможен незаполненный черновик.
    /// </remarks>
    /// <exception cref="ArgumentException">У получателя отсутствует идентификатор или заполненный email.</exception>
    Task<Guid> CreateCampaignAsync(string name, string messageText, CancellationToken ct);

    /// <summary>Изменяет название и сообщение черновика и возвращает сохранённую кампанию.</summary>
    /// <exception cref="KeyNotFoundException">Кампания не найдена.</exception>
    /// <exception cref="InvalidOperationException">Кампания уже запущена или завершена.</exception>
    /// <exception cref="ArgumentException">Название или сообщение не заполнены.</exception>
    Task<Campaign> UpdateCampaignAsync(Guid campaignId, string name, string messageText, CancellationToken ct);

    /// <summary>Удаляет кампанию, если она не находится в обработке.</summary>
    /// <returns>True, если кампания удалена; false, если она не найдена.</returns>
    /// <exception cref="InvalidOperationException">Кампания находится в обработке.</exception>
    Task<bool> DeleteCampaignAsync(Guid campaignId, CancellationToken ct);

    /// <summary>Запускает черновик; повторный запуск запущенной кампании не изменяет даты или доставки.</summary>
    /// <exception cref="KeyNotFoundException">Кампания не найдена.</exception>
    /// <exception cref="InvalidStatusTransitionException">Текущий статус не допускает запуск.</exception>
    /// <exception cref="ArgumentException">Название или сообщение не заполнены.</exception>
    Task StartCampaignAsync(Guid campaignId, CancellationToken ct);

    /// <summary>Возвращает кампанию или null, если она не найдена.</summary>
    Task<Campaign?> GetCampaignAsync(Guid campaignId, CancellationToken ct);

    /// <summary>Возвращает доставку или null, если она не найдена.</summary>
    Task<Delivery?> GetDeliveryAsync(Guid deliveryId, CancellationToken ct);

    /// <summary>Возвращает число доставок кампании по статусам.</summary>
    Task<DeliveryStatusCounts> GetDeliveryStatusCountsAsync(Guid campaignId, CancellationToken ct);

    /// <summary>Возвращает получателя или null, если он не найден.</summary>
    Task<Recipient?> GetRecipientAsync(Guid recipientId, CancellationToken ct);

    /// <summary>Возвращает страницу всех получателей в порядке Id.</summary>
    Task<PageResult<Recipient>> GetRecipientsAsync(int pageNumber, int pageSize, CancellationToken ct);

    /// <summary>Возвращает страницу кампаний в порядке Id и общее число кампаний.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Номер или размер страницы меньше 1.</exception>
    Task<PageResult<Campaign>> GetCampaignsAsync(int pageNumber, int pageSize, CancellationToken ct);

    /// <summary>Возвращает страницу доставок кампании в порядке Id и общее число её доставок.</summary>
    /// <remarks>Для отсутствующей кампании возвращается пустая страница.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Номер или размер страницы меньше 1.</exception>
    Task<PageResult<Delivery>> GetDeliveriesAsync(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct);

    /// <summary>Возвращает страницу уникальных получателей кампании в порядке Id и их общее число.</summary>
    /// <remarks>Для отсутствующей кампании возвращается пустая страница.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">Номер или размер страницы меньше 1.</exception>
    Task<PageResult<Recipient>> GetRecipientsAsync(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct);
}
