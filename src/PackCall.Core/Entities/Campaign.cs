using System;
using System.Collections.Generic;

namespace PackCall.Core.Entities
{
    public class Campaign
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string MessageText { get; set; }
        public CampaignStatus Status { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();
    }

    public enum CampaignStatus
    {
        Draft = 0,
        InProgress = 1,
        Completed = 2,
        Failed = -1
    }
}