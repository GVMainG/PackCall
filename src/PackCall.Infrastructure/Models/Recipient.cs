using System;
using System.Collections.Generic;

namespace PackCall.Infrastructure.Models
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
