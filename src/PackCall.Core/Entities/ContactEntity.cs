using System;
using System.Collections.Generic;
using System.Text;

namespace PackCall.Core.Entities
{
    internal class ContactEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; }

        public IReadOnlyDictionary<ContactType, string> Type { get; set; }

        public enum ContactType
        {
            Email
        }
    }
}
