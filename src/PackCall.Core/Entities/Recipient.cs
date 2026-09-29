using System;
using System.Collections.Generic;

namespace PackCall.Core.Entities
{
    public class Recipient
    {
        public Guid Id { get; set; }
        public string Email { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();
    }
}
