using PackCall.Core.Models;
using PackCall.Core.Repositorys;

namespace PackCall.Core.Services;

public sealed class CampaignService
{
    private readonly ICampaingRepository _campaignRepository;
    private readonly IDeliveryRepository _deliveryRepository;

    public CampaignService(ICampaingRepository campaignRepository, IDeliveryRepository deliveryRepository)
    {
        _campaignRepository = campaignRepository;
        _deliveryRepository = deliveryRepository;
    }

    public async Task<Guid> CreateCampaignAsync(string name, string messageText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var campaign = Campaign.Create(name, messageText);
        var created = await _campaignRepository.Create(campaign, ct);
        return created.Id;
    }

    public async Task<Campaign> UpdateCampaignAsync(Guid campaignId, string name, string messageText, CancellationToken ct)
    {
        var campaign = await GetRequiredCampaignAsync(campaignId, ct);
        campaign.UpdateDetails(name, messageText, DateTime.UtcNow);
        ct.ThrowIfCancellationRequested();
        return await _campaignRepository.Update(campaign, ct);
    }

    public async Task<bool> DeleteCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await GetCampaignAsync(campaignId, ct);
        if (campaign is null)
            return false;
        if (campaign.Status == CampaignStatus.InProgress)
            throw new InvalidOperationException("Нельзя удалить запущенную кампанию.");

        ct.ThrowIfCancellationRequested();
        return await _campaignRepository.Delete(campaignId, ct);
    }

    public async Task StartCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await GetRequiredCampaignAsync(campaignId, ct);
        campaign.Start(DateTime.UtcNow);
        ct.ThrowIfCancellationRequested();
        await _campaignRepository.Update(campaign, ct);
    }

    public async Task<Campaign?> GetCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var campaigns = await _campaignRepository.Get(campaignId, ct);
        return campaigns.SingleOrDefault();
    }

    public Task<PageResult<Campaign>> GetCampaignsAsync(int pageNumber, int pageSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _campaignRepository.GetPage(pageNumber, pageSize, ct);
    }

    public Task<PageResult<DeliveryModel>> GetDeliveriesAsync(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _deliveryRepository.GetPage(campaignId, pageNumber, pageSize, ct);
    }

    public async Task<PageResult<RecipientModel>> GetRecipientsAsync(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        ct.ThrowIfCancellationRequested();

        var deliveries = await _deliveryRepository.Get(campaignId, _ => true, ct);

        ct.ThrowIfCancellationRequested();

        var recipients = deliveries.Select(delivery => delivery.Recipient)
            .DistinctBy(recipient => recipient.Id)
            .OrderBy(recipient => recipient.Id)
            .ToList();
        var items = recipients.Skip(checked((pageNumber - 1) * pageSize)).Take(pageSize).ToList();
        
        return new PageResult<RecipientModel>(items, recipients.Count);
    }

    private async Task<Campaign> GetRequiredCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        return await GetCampaignAsync(campaignId, ct)
            ?? throw new KeyNotFoundException($"Кампания {campaignId} не найдена.");
    }
}
