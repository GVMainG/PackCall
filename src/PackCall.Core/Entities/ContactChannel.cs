namespace PackCall.Core.Entities;
public sealed class ContactChannel
{
 public Guid Id { get; set; }
 public Guid ContactId { get; set; }
 public string Channel { get; set; } = null!;
 public string Address { get; set; } = null!;
 public Contact Contact { get; set; } = null!;
}
