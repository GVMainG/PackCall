using System;

namespace PackCall.Core.Entities
{
    public class Delivery
    {
        public Guid Id { get; set; }
        public Guid CampaignId { get; set; }
        public Guid RecipientId { get; set; }
        public string RecipientEmail { get; set; }
        public DeliveryStatus DeliveryStatus { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string WorkerId { get; set; }

        public Campaign Campaign { get; set; }
        public Recipient Recipient { get; set; }
    }

    public enum DeliveryStatus
    {
        Failed = -1,
        Waiting = 0,
        InProgress = 1,
        Sent = 2
    }
}
