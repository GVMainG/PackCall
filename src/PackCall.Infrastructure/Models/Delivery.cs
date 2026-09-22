using System;

namespace PackCall.Infrastructure.Models
{
    public class Delivery
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }
        public Guid RecipientId { get; set; }
        public string RecipientEmail { get; set; }
        public string DeliveryStatus { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public Campaign Campaign { get; set; }
        public Recipient Recipient { get; set; }
    }
}
