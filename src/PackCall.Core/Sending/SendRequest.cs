namespace PackCall.Core.Sending;

/// <summary>
/// Данные одной отправки сообщения.
/// </summary>
/// <param name="DeliveryId">Идентификатор обрабатываемой доставки.</param>
/// <param name="CampaignId">Идентификатор кампании.</param>
/// <param name="ContactId">Идентификатор получателя.</param>
/// <param name="Channel">Сохранённый канал доставки.</param>
/// <param name="Address">Снимок адреса получателя.</param>
/// <param name="MessageText">Текст сообщения кампании.</param>
public sealed record SendRequest(
    Guid DeliveryId,
    Guid CampaignId,
    Guid ContactId,
    string Channel,
    string Address,
    string MessageText);
