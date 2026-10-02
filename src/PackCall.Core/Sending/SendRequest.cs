namespace PackCall.Core.Sending;

/// <summary>Данные одной отправки сообщения.</summary>
/// <param name="DeliveryId">Идентификатор доставки — ключ идемпотентности для оператора.</param>
/// <param name="CampaignId">Идентификатор кампании.</param>
/// <param name="RecipientId">Идентификатор получателя.</param>
/// <param name="RecipientEmail">Снимок адреса получателя.</param>
/// <param name="MessageText">Текст сообщения кампании.</param>
public sealed record SendRequest(
    Guid DeliveryId,
    Guid CampaignId,
    Guid RecipientId,
    string RecipientEmail,
    string MessageText);
