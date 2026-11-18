namespace PackCall.Core.Entities;
public sealed class CampaignContact
{
    public CampaignContact(Guid campaignId, Guid contactId)
    {
        CampaignId = campaignId;
        ContactId = contactId;
    }

    public Guid CampaignId { get; private set; }
    public Guid ContactId { get; private set; }
}
