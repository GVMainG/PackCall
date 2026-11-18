using System.ComponentModel.DataAnnotations;

namespace PackCall.Api.Models;

/// <summary>Адрес контакта в канале. Telegram chat ID передаётся строкой.</summary>
public sealed record ContactChannelRequest([Required] string Channel, [Required] string Address);

/// <summary>Имя и адреса нового контакта.</summary>
public sealed record CreateContactRequest(
    [Required, MinLength(1)] ContactChannelRequest?[] Channels,
    [MaxLength(255)] string? DisplayName = null);

/// <summary>Полный набор адресов для замены.</summary>
public sealed record ReplaceContactAddressesRequest([Required, MinLength(1)] ContactChannelRequest?[] Channels);

/// <summary>Нормализованный адрес контакта в канале.</summary>
public sealed record ContactChannelResponse(string Channel, string Address);

/// <summary>Контакт и его адреса.</summary>
public sealed record ContactResponse(
    Guid Id, string? DisplayName, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt,
    IReadOnlyList<ContactChannelResponse> Channels);
