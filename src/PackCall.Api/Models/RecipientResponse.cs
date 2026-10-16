namespace PackCall.Api.Models;

/// <summary>Получатель из статичной аудитории.</summary>
public sealed record RecipientResponse(
    Guid Id,
    string Email,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);
