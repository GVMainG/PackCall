using System;
using System.Collections.Generic;
using System.Text;

namespace PackCall.Core.Entities
{
    internal class CampaignEntity
    {
        public Guid Id { get; set; }
        public string Title { get; set; }
        public CampaignStatus Status { get; set; }

    }

    public enum CampaignStatus
    {
        Draft,
        Running,
        Completed,
        Cancelled
    }
}
