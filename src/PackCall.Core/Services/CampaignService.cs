using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using PackCall.Core.Exceptions;
using System.Net.Mail;

namespace PackCall.Core.Services;

public sealed class CampaignService : ICampaignService
{
    private const int FormationBatchSize = 500;
    private readonly ICampaingRepository _campaignRepository;
    private readonly IDeliveryRepository _deliveryRepository;
    private readonly IRecipientRepository _recipientRepository;

    public CampaignService(
        ICampaingRepository campaignRepository,
        IDeliveryRepository deliveryRepository,
        IRecipientRepository recipientRepository)
    {
        _campaignRepository = campaignRepository;
        _deliveryRepository = deliveryRepository;
        _recipientRepository = recipientRepository;
    }

    public async Task<Guid> CreateCampaignAsync(string name, string messageText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var campaign = Campaign.Create(name, messageText);
        campaign.StatusFromDraftToForming();
        await _campaignRepository.Create(campaign, ct);

        try
        {
            var offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var recipients = await _recipientRepository.GetBatchAsync(offset, FormationBatchSize, ct);
                if (recipients.Count == 0)
                    break;

                var now = DateTime.UtcNow;
                var deliveries = recipients.Select(recipient => Delivery.Create(campaign, recipient, now)).ToList();
                ct.ThrowIfCancellationRequested();
                await _deliveryRepository.AddBatchAsync(deliveries, ct);
                offset = checked(offset + recipients.Count);
                if (recipients.Count < FormationBatchSize)
                    break;
            }

            ct.ThrowIfCancellationRequested();
            campaign.StatusFromFormingToDraft();
            await _campaignRepository.Update(campaign, ct);
            return campaign.Id;
        }
        catch (Exception creationError)
        {
            try
            {
                await _campaignRepository.Delete(campaign.Id, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Не удалось удалить частично сформированную кампанию.",
                    creationError, cleanupError);
            }
            throw;
        }
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
        if (campaign.Status is CampaignStatus.InProgress or CampaignStatus.Paused)
            throw new InvalidOperationException("Нельзя удалить запущенную или приостановленную кампанию.");

        ct.ThrowIfCancellationRequested();
        return await _campaignRepository.Delete(campaignId, ct);
    }

    public async Task StartCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await GetRequiredCampaignAsync(campaignId, ct);

        if (campaign.Status == CampaignStatus.InProgress)
            return;

        ct.ThrowIfCancellationRequested();
        var previousStatus = campaign.Status;
        campaign.StatusFromDraftToInProgress();
        ct.ThrowIfCancellationRequested();

        await SaveStatusChangeAsync(campaign, previousStatus, ct);
    }

    public async Task PauseCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await GetRequiredCampaignAsync(campaignId, ct);
        var previousStatus = campaign.Status;
        campaign.StatusFromInProgressToPaused(DateTime.UtcNow);
        await SaveStatusChangeAsync(campaign, previousStatus, ct);
    }

    public async Task ResumeCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await GetRequiredCampaignAsync(campaignId, ct);
        var previousStatus = campaign.Status;
        campaign.StatusFromPausedToInProgress(DateTime.UtcNow);
        await SaveStatusChangeAsync(campaign, previousStatus, ct);

        if (!await _deliveryRepository.HasPendingDeliveriesAsync(campaignId, ct))
        {
            campaign.StatusFromInProgressToCompleted(DateTime.UtcNow);
            await _campaignRepository.TryUpdateStatusAsync(campaign, CampaignStatus.InProgress, ct);
        }
    }

    private async Task SaveStatusChangeAsync(Campaign campaign, CampaignStatus previousStatus, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (previousStatus == campaign.Status ||
            await _campaignRepository.TryUpdateStatusAsync(campaign, previousStatus, ct))
            return;

        var current = await GetRequiredCampaignAsync(campaign.Id, ct);
        if (current.Status != campaign.Status)
            throw new DomainException("Статус кампании изменился другим запросом. Повторите операцию.");
    }

    public async Task<Campaign?> GetCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var campaigns = await _campaignRepository.Get(campaignId, ct);
        return campaigns.SingleOrDefault();
    }

    public async Task<Delivery?> GetDeliveryAsync(Guid deliveryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return (await _deliveryRepository.Get(deliveryId, ct)).SingleOrDefault();
    }

    public Task<DeliveryStatusCounts> GetDeliveryStatusCountsAsync(Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _deliveryRepository.GetStatusCountsAsync(campaignId, ct);
    }

    public Task<Recipient?> GetRecipientAsync(Guid recipientId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _recipientRepository.GetAsync(recipientId, ct);
    }

    public async Task<Recipient> CreateRecipientAsync(string email, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Укажите email получателя.", nameof(email));

        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (normalizedEmail.Length > 255 ||
            !MailAddress.TryCreate(normalizedEmail, out var address) ||
            !string.Equals(address.Address, normalizedEmail, StringComparison.Ordinal))
            throw new ArgumentException("Укажите корректный email длиной не более 255 символов.", nameof(email));

        if (await _recipientRepository.GetByEmailAsync(normalizedEmail, ct) is not null)
            throw new RecipientAlreadyExistsException();

        ct.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        return await _recipientRepository.CreateAsync(new Recipient
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            CreatedAt = now,
            UpdatedAt = now
        }, ct);
    }

    public Task<PageResult<Recipient>> GetRecipientsAsync(int pageNumber, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ct.ThrowIfCancellationRequested();
        return _recipientRepository.GetPageAsync(pageNumber, pageSize, ct);
    }

    public Task<PageResult<Campaign>> GetCampaignsAsync(int pageNumber, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ct.ThrowIfCancellationRequested();
        return _campaignRepository.GetPage(pageNumber, pageSize, ct);
    }

    public Task<PageResult<Delivery>> GetDeliveriesAsync(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ct.ThrowIfCancellationRequested();
        return _deliveryRepository.GetPage(campaignId, pageNumber, pageSize, ct);
    }

    public async Task<PageResult<Recipient>> GetRecipientsAsync(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct)
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

        return new PageResult<Recipient>(items, recipients.Count);
    }

    private async Task<Campaign> GetRequiredCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        return await GetCampaignAsync(campaignId, ct)
            ?? throw new KeyNotFoundException($"Кампания {campaignId} не найдена.");
    }
}
