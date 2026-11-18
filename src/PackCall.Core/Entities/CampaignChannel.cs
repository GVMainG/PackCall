namespace PackCall.Core.Entities;
public sealed class CampaignChannel
{
    public CampaignChannel(Guid campaignId, string channel)
    {
        CampaignId = campaignId;
        Channel = channel;
    }

    public Guid CampaignId { get; private set; }
    public string Channel { get; private set; }
}
