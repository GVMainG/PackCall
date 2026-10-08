using PackCall.Core.Models;

namespace PackCall.Core.Journaling;

/// <summary>Запись журнала о смене статуса доставки.</summary>
/// <param name="CampaignId">Идентификатор кампании.</param>
/// <param name="DeliveryId">Идентификатор доставки.</param>
/// <param name="PreviousStatus">Прежний статус доставки.</param>
/// <param name="NewStatus">Новый статус доставки.</param>
/// <param name="WorkerId">Идентификатор воркера; заполняется для захвата.</param>
/// <param name="OccurredAt">Время события в UTC.</param>
/// <param name="OperatorResult">Результат вызова mock-оператора, если вызов выполнялся.</param>
public sealed record StatusChangeEntry(
    Guid CampaignId,
    Guid DeliveryId,
    DeliveryStatus PreviousStatus,
    DeliveryStatus NewStatus,
    string? WorkerId,
    DateTime OccurredAt,
    string? OperatorResult);
