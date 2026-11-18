using System.Globalization;
using System.Net.Mail;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Entities;

public sealed class ContactChannel
{
    public const string Email = "email";
    public const string Telegram = "telegram";

    private ContactChannel() { }

    public Guid Id { get; private set; }
    public Guid ContactId { get; private set; }
    public string Channel { get; private set; } = null!;
    public string Address { get; private set; } = null!;
    public Contact Contact { get; private set; } = null!;

    internal static ContactChannel Create(string channel, string? address)
    {
        var value = address?.Trim() ?? "";
        switch (channel)
        {
            case Email:
                value = value.ToLowerInvariant();
                if (value.Length > 255 || value.Contains('\0') || !MailAddress.TryCreate(value, out var email) ||
                    !string.Equals(email.Address, value, StringComparison.Ordinal))
                    throw new ContactValidationException("Укажите корректный email длиной не более 255 символов.");
                break;
            case Telegram:
                if (value.Contains('\0') ||
                    !long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chatId) ||
                    chatId == 0)
                    throw new ContactValidationException("Telegram chat ID должен быть ненулевым целым числом в диапазоне Int64.");
                value = chatId.ToString(CultureInfo.InvariantCulture);
                break;
            default:
                throw new ContactValidationException("Неизвестный канал контакта.");
        }

        return new ContactChannel { Channel = channel, Address = value };
    }

    internal void UpdateAddress(ContactChannel validated) => Address = validated.Address;
}
