using System.ComponentModel.DataAnnotations;
using System.Text;
using PackCall.Core.Entities;

namespace PackCall.Api.Models;

/// <summary>Данные для создания кампании по снимку контактов и каналов.</summary>
public sealed record CreateCampaignRequest : IValidatableObject
{
    /// <summary>Название кампании. От 1 до 255 символов, включая хотя бы один непробельный.</summary>
    [Required(ErrorMessage = "Укажите название кампании.")]
    [UnicodeStringLength(255, MinimumLength = 1, ErrorMessage = "Название кампании должно содержать от 1 до 255 символов.")]
    public string Name { get; init; } = null!;

    /// <summary>Непустой текст сообщения. Для кампании с telegram допускается до 4096 символов включительно.</summary>
    [Required(ErrorMessage = "Укажите текст сообщения.")]
    public string MessageText { get; init; } = null!;

    /// <summary>Режим выбора контактов: all или contactIds.</summary>
    [Required(ErrorMessage = "Укажите режим выборки.")]
    [RegularExpression("^(all|contactIds)$", ErrorMessage = "Допустимые режимы выборки: all и contactIds.")]
    public string Selection { get; init; } = null!;

    /// <summary>Непустой массив UUID в режиме contactIds. В режиме all поле отсутствует или равно null.</summary>
    [MinLength(1, ErrorMessage = "Укажите хотя бы один контакт.")]
    public Guid[]? ContactIds { get; init; }

    /// <summary>Непустой массив каналов. Допустимые значения: email, telegram.</summary>
    [Required(ErrorMessage = "Укажите каналы кампании.")]
    [MinLength(1, ErrorMessage = "Укажите хотя бы один канал.")]
    public string[] Channels { get; init; } = null!;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Name.Contains('\0'))
            yield return new("Название кампании содержит нулевой символ.", [nameof(Name)]);
        if (MessageText.Contains('\0'))
            yield return new("Текст сообщения содержит нулевой символ.", [nameof(MessageText)]);
        if (Selection == "all" && ContactIds is not null)
            yield return new("В режиме all поле contactIds должно отсутствовать или быть равно null.", [nameof(ContactIds)]);
        if (Selection == "contactIds" && ContactIds is null)
            yield return new("В режиме contactIds укажите хотя бы один контакт.", [nameof(ContactIds)]);
        if (Channels.Any(channel => channel is null ||
            !ContactChannel.SupportedChannels.Contains(channel, StringComparer.Ordinal)))
            yield return new("Допустимые каналы: email и telegram.", [nameof(Channels)]);
        if (Channels.Contains(ContactChannel.Telegram) && MessageText.EnumerateRunes().Count() > 4096)
            yield return new("Для telegram текст сообщения должен содержать не более 4096 символов.", [nameof(MessageText)]);
    }
}

file sealed class UnicodeStringLengthAttribute(int maximumLength) : StringLengthAttribute(maximumLength)
{
    public override bool IsValid(object? value)
    {
        if (value is not string text)
            return value is null;

        var length = text.EnumerateRunes().Count();
        return length >= MinimumLength && length <= MaximumLength;
    }
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
