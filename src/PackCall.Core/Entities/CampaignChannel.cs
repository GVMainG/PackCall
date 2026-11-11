namespace PackCall.Core.Entities;
public sealed class CampaignChannel
{
 public Guid CampaignId { get; set; }
 public string Channel { get; set; } = null!;
}
