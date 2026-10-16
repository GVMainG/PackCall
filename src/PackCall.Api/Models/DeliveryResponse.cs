using PackCall.Core.Entities;

namespace PackCall.Api.Models;

/// <summary>Состояние доставки.</summary>
public sealed record DeliveryResponse(
    Guid Id,
    Guid CampaignId,
    Guid RecipientId,
    string RecipientEmail,
    DeliveryStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? WorkerId,
    DateTimeOffset? LockExpiresAt);
