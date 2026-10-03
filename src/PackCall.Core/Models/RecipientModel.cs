namespace PackCall.Core.Models
{
    public class RecipientModel
    {
        public Guid Id { get; set; }
        public string Email { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public ICollection<DeliveryModel> Deliveries { get; set; } = new List<DeliveryModel>();
    }
}
