using System.ComponentModel.DataAnnotations.Schema;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Entities;

public sealed class Contact
{
    private readonly List<ContactChannel> _channels = new();

    private Contact() { }

    public Guid Id { get; private set; }
    public string? DisplayName { get; set; }
    public DateTime? CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public IReadOnlyCollection<ContactChannel> Channels => _channels.AsReadOnly();
    public ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();

    // Временное свойство для совместимости. Адрес берётся из канала и отдельно не хранится.
    [NotMapped]
    public string Email => _channels.Single(c => c.Channel == ContactChannel.Email).Address;

    public static Contact Create(IEnumerable<(string Channel, string? Address)> addresses, DateTime now)
    {
        var contact = new Contact { Id = Guid.NewGuid(), CreatedAt = now };
        contact.ReplaceAddresses(addresses, now);
        return contact;
    }

    public void ReplaceAddresses(IEnumerable<(string Channel, string? Address)> addresses, DateTime now)
    {
        if (addresses is null)
            throw new ContactValidationException("Укажите хотя бы один адрес контакта.");

        var validated = new Dictionary<string, ContactChannel>();
        foreach (var (channel, address) in addresses)
        {
            var entry = ContactChannel.Create(channel, address);
            if (!validated.TryAdd(entry.Channel, entry))
                throw new ContactValidationException("У контакта может быть только один адрес на канал.");
        }

        if (validated.Count == 0)
            throw new ContactValidationException("Укажите хотя бы один адрес контакта.");

        _channels.RemoveAll(channel => !validated.ContainsKey(channel.Channel));
        foreach (var current in _channels)
        {
            current.UpdateAddress(validated[current.Channel]);
            validated.Remove(current.Channel);
        }
        _channels.AddRange(validated.Values);
        UpdatedAt = now;
    }
}
