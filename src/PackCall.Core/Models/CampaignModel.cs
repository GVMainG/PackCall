namespace PackCall.Core.Models
{
    public class CampaignModel
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string MessageText { get; set; }
        public CampaignStatus Status { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        public ICollection<DeliveryModel> Deliveries { get; set; } = new List<DeliveryModel>();
    }

    public enum CampaignStatus
    {
        Draft = 0,
        InProgress = 1,
        Completed = 2,
        Failed = -1
    }
}