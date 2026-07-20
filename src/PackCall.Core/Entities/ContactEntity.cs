namespace PackCall.Core.Entities
{
    internal class ContactEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; }

        public IReadOnlyDictionary<ContactType, string> Channels { get; set; }

        public DateTimeOffset CreatedAt { get; set; }

        public enum ContactType
        {
            Email
        }

        public ContactEntity(string name, IReadOnlyDictionary<ContactType, string> channels, DateTimeOffset createdAt)
        {
            Id = Guid.NewGuid();
            Name = name;
            Channels = channels;
            CreatedAt = DateTime.Now;
        }
    }
}