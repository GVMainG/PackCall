using System;
using System.Collections.Generic;
using System.Text;

namespace PackCall.Core.Entities
{
    internal class DeliveryEntity
    {
        public Guid Id { get; set; }

        public Guid CampaignId { get; set; }

        public Guid ContactId { get; set; }

        public string ContactSnapshot { get; set; }

        public enum DeliveryChannel
        {
            Email
        }

    }
}
