using PackCall.Core.Entities;

namespace PackCall.Api.Models;

/// <summary>Данные для создания черновика кампании.</summary>
public sealed record CreateCampaignRequest
{
    public string? Name { get; init; }
    public string? MessageText { get; init; }
    /// <summary>Каналы кампании. Если поле пропущено или равно null, используется email.</summary>
    public IReadOnlyList<string?>? Channels { get; init; }
}

/// <summary>Изменяемые поля черновика кампании; пропущенное поле сохраняет прежнее значение.</summary>
public sealed record UpdateCampaignRequest
{
    public string? Name { get; init; }
    public string? MessageText { get; init; }
}

/// <summary>Кампания.</summary>
public sealed record CampaignResponse(
    Guid Id,
    string Name,
    string MessageText,
    CampaignStatus Status,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>Количество доставок кампании по статусам.</summary>
public sealed record DeliveryStatisticsResponse(
    int Total, int Waiting, int InProgress, int Sent, int Failed);

/// <summary>Итоги формирования по сохранённым контактам, каналам и доставкам.</summary>
/// <param name="SkippedDeliveryCount">Число выбранных пар без доставки. В статусе Forming равно null.</param>
public sealed record CampaignFormationResponse(
    long SelectedContactCount, IReadOnlyList<string> Channels, int CreatedDeliveryCount, long? SkippedDeliveryCount);

/// <summary>Кампания со статистикой доставок и итогами формирования.</summary>
public sealed record CampaignDetailsResponse(
    CampaignResponse Campaign, DeliveryStatisticsResponse Deliveries, CampaignFormationResponse Formation);
