using System.ComponentModel.DataAnnotations.Schema;
namespace PackCall.Core.Entities;
public sealed class Contact
{
    public Guid Id { get; set; }
    public string? DisplayName { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public ICollection<ContactChannel> Channels { get; set; } = new List<ContactChannel>();
    public ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();
    // Transitional email projection; never stored as a second address.
    [NotMapped]
    public string Email
    {
        get => Channels.Single(c => c.Channel == "email").Address;
        set
        {
            var channel = Channels.SingleOrDefault(c => c.Channel == "email");
            if (channel is null) Channels.Add(new ContactChannel { Id = Guid.NewGuid(), ContactId = Id, Channel = "email", Address = value.Trim().ToLowerInvariant() });
            else channel.Address = value.Trim().ToLowerInvariant();
        }
    }
}
