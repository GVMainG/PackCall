namespace PackCall.Api.Models;

/// <summary>Данные для добавления получателя в аудиторию будущих кампаний.</summary>
public sealed record CreateRecipientRequest
{
    public string? Email { get; init; }
}

/// <summary>Получатель из статичной аудитории.</summary>
public sealed record RecipientResponse(
    Guid Id,
    string Email,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);
