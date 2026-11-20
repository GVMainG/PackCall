using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Services;

// Управляет жизненным циклом кампании и формирует очередь доставок для её аудитории.
public sealed class CampaignService(
    ICampaingRepository campaignRepository,
    IDeliveryRepository deliveryRepository,
    IContactRepository recipientRepository,
    IChannelOperatorAvailability operators) : ICampaignService
{
    private const int FormationBatchSize = 500;

    public async Task<Guid> CreateCampaignSnapshotAsync(string name, string messageText,
        IReadOnlyList<Guid>? contactIds, IReadOnlyList<string?> channels, CancellationToken ct)
    {
        var (contacts, channelSnapshot) = ValidateSelection(contactIds, channels, ct);
        var campaign = CreateFormingCampaign(name, messageText);
        if (!await campaignRepository.CreateWithSnapshotAsync(campaign, contacts, channelSnapshot, ct))
            throw new ArgumentException("Один или несколько выбранных контактов не найдены.", nameof(contactIds));
        return campaign.Id;
    }

    public async Task<Campaign> CreateCampaignAsync(string name, string messageText,
        IReadOnlyList<Guid>? contactIds, IReadOnlyList<string?> channels, CancellationToken ct)
    {
        var (contacts, channelSnapshot) = ValidateSelection(contactIds, channels, ct);
        var campaign = CreateFormingCampaign(name, messageText);
        var formed = await CompleteFormationAsync(campaign, async () =>
        {
            if (!await campaignRepository.CreateWithSnapshotAsync(campaign, contacts, channelSnapshot, ct))
                return false;
            await FormDeliveriesFromSnapshotAsync(campaign.Id, ct);
            return true;
        }, ct);
        if (!formed)
            throw new CampaignContactsNotFoundException();
        return campaign;
    }

    private static (Guid[]? Contacts, List<string> Channels) ValidateSelection(
        IReadOnlyList<Guid>? contactIds, IReadOnlyList<string?> channels, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(channels);
        var selectedContacts = contactIds?.Distinct().ToArray();
        if (selectedContacts is { Length: 0 })
            throw new ArgumentException("Укажите хотя бы один контакт.", nameof(contactIds));

        var selectedChannels = channels.Distinct(StringComparer.Ordinal).ToArray();
        if (selectedChannels.Length == 0)
            throw new ArgumentException("Укажите хотя бы один канал.", nameof(channels));

        var channelSnapshot = new List<string>(selectedChannels.Length);
        foreach (var channel in selectedChannels)
        {
            if (channel is null || !ContactChannel.SupportedChannels.Contains(channel, StringComparer.Ordinal))
                throw new ArgumentException($"Неизвестный канал: '{channel}'.", nameof(channels));
            channelSnapshot.Add(channel);
        }

        return (selectedContacts, channelSnapshot);
    }

    public Task<int> FormDeliveriesFromSnapshotAsync(Guid campaignId, CancellationToken ct) =>
        deliveryRepository.FormDeliveriesFromSnapshotAsync(campaignId, ct);

    public async Task<Guid> CreateCampaignAsync(string name, string messageText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var campaign = CreateFormingCampaign(name, messageText);
        await CompleteFormationAsync(campaign, async () =>
        {
            await campaignRepository.Create(campaign, ct);
            await FormLegacyEmailDeliveriesAsync(campaign, ct);
            return true;
        }, ct);
        return campaign.Id;
    }

    private static Campaign CreateFormingCampaign(string name, string messageText)
    {
        var campaign = Campaign.Create(name, messageText);
        campaign.StatusFromDraftToForming();
        return campaign;
    }

    private async Task<bool> CompleteFormationAsync(Campaign campaign, Func<Task<bool>> saveAndForm, CancellationToken ct)
    {
        try
        {
            if (!await saveAndForm())
                return false;
            ct.ThrowIfCancellationRequested();
            campaign.StatusFromFormingToDraft();
            if (!await campaignRepository.TryPublishDraftAsync(campaign, ct))
                throw new InvalidOperationException("Не удалось перевести формируемую кампанию в черновик.");
            return true;
        }
        catch (Exception creationError)
        {
            try
            {
                // Отмена запроса не должна прерывать удаление уже сохранённой части кампании.
                await campaignRepository.DeleteIfFormingAsync(campaign.Id, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                throw new CampaignFormationException(new AggregateException(
                    "Не удалось удалить частично сформированную кампанию.", creationError, cleanupError));
            }
            if (creationError is OperationCanceledException && ct.IsCancellationRequested)
                throw;
            throw new CampaignFormationException(creationError);
        }
    }

    private async Task FormLegacyEmailDeliveriesAsync(Campaign campaign, CancellationToken ct)
    {
        // Пакеты ограничивают объём контактов и доставок в памяти одного запроса.
        var offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var recipients = await recipientRepository.GetBatchAsync(offset, FormationBatchSize, ct);
            if (recipients.Count == 0)
                break;

            var now = DateTime.UtcNow;
            var deliveries = recipients.Select(recipient => Delivery.Create(campaign, recipient, now)).ToList();
            ct.ThrowIfCancellationRequested();
            await deliveryRepository.AddLegacyEmailBatchAsync(deliveries, ct);
            offset = checked(offset + recipients.Count);
            if (recipients.Count < FormationBatchSize)
                break;
        }
    }

    public async Task<Campaign> UpdateCampaignAsync(Guid campaignId, string name, string messageText, CancellationToken ct)
    {
        var campaign = await GetRequiredCampaignAsync(campaignId, ct);
        campaign.UpdateDetails(name, messageText, DateTime.UtcNow);
        ct.ThrowIfCancellationRequested();
        return await campaignRepository.Update(campaign, ct);
    }

    public async Task<bool> DeleteCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await campaignRepository.DeleteIfInactiveAsync(campaignId, ct);
        if (result == CampaignDeleteResult.Active)
            throw new InvalidOperationException("Нельзя удалить запущенную или приостановленную кампанию.");
        return result == CampaignDeleteResult.Deleted;
    }

    public async Task<Campaign> StartCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await GetRequiredCampaignAsync(campaignId, ct);

        if (campaign.Status == CampaignStatus.InProgress)
            return campaign;

        ct.ThrowIfCancellationRequested();
        var previousStatus = campaign.Status;
        if (previousStatus == CampaignStatus.Draft)
        {
            var channels = await campaignRepository.GetChannelsAsync(campaignId, ct);
            foreach (var channel in channels)
                if (!operators.HasOperator(channel))
                    throw new ChannelOperatorUnavailableException(channel);
        }
        ct.ThrowIfCancellationRequested();
        campaign.StatusFromDraftToInProgress();
        return await ActivateCampaignAsync(campaign, previousStatus, ct);
    }

    public async Task<Campaign> PauseCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await GetRequiredCampaignAsync(campaignId, ct);
        var previousStatus = campaign.Status;
        campaign.StatusFromInProgressToPaused(DateTime.UtcNow);
        return await SaveStatusChangeAsync(campaign, previousStatus, ct);
    }

    public async Task<Campaign> ResumeCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var campaign = await GetRequiredCampaignAsync(campaignId, ct);
        var previousStatus = campaign.Status;
        campaign.StatusFromPausedToInProgress(DateTime.UtcNow);
        return await ActivateCampaignAsync(campaign, previousStatus, ct);
    }

    private async Task<Campaign> ActivateCampaignAsync(Campaign campaign, CampaignStatus previousStatus, CancellationToken ct)
    {
        if (!await deliveryRepository.HasPendingDeliveriesAsync(campaign.Id, ct))
            campaign.StatusFromInProgressToCompleted(DateTime.UtcNow);

        var saved = await SaveStatusChangeAsync(campaign, previousStatus, ct);
        if (previousStatus != CampaignStatus.Paused || saved.Status != CampaignStatus.InProgress)
            return saved;

        // Последнее подтверждение могло прийти на паузе. После записи возобновления завершаем проверку независимо от запроса.
        if (await deliveryRepository.HasPendingDeliveriesAsync(saved.Id, CancellationToken.None))
            return saved;

        var current = await campaignRepository.Get(saved.Id, CancellationToken.None);
        if (current?.Status != CampaignStatus.InProgress)
            return current ?? saved;

        current.StatusFromInProgressToCompleted(DateTime.UtcNow);
        if (await campaignRepository.TryUpdateStatusAsync(current, CampaignStatus.InProgress, CancellationToken.None))
            return current;
        return await campaignRepository.Get(saved.Id, CancellationToken.None) ?? saved;
    }

    private async Task<Campaign> SaveStatusChangeAsync(Campaign campaign, CampaignStatus previousStatus, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (previousStatus == campaign.Status ||
            await campaignRepository.TryUpdateStatusAsync(campaign, previousStatus, ct))
            return campaign;

        var current = await GetRequiredCampaignAsync(campaign.Id, ct);
        if (previousStatus == CampaignStatus.Paused &&
            current.Status is CampaignStatus.InProgress or CampaignStatus.Completed)
            return current;
        if (current.Status == campaign.Status &&
            (previousStatus != CampaignStatus.Draft || campaign.Status != CampaignStatus.Completed))
            return current;
        throw new DomainException("Статус кампании изменился другим запросом. Повторите операцию.");
    }

    public async Task<Campaign?> GetCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await campaignRepository.Get(campaignId, ct);
    }

    public Task<CampaignDetails?> GetCampaignDetailsAsync(Guid campaignId, CancellationToken ct) =>
        campaignRepository.GetDetailsAsync(campaignId, ct);

    public async Task<Delivery?> GetDeliveryAsync(Guid deliveryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await deliveryRepository.Get(deliveryId, ct);
    }

    public Task<DeliveryStatusCounts> GetDeliveryStatusCountsAsync(Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return deliveryRepository.GetStatusCountsAsync(campaignId, ct);
    }

    public Task<Contact?> GetRecipientAsync(Guid recipientId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return recipientRepository.GetAsync(recipientId, ct);
    }

    public async Task<Contact> CreateRecipientAsync(string email, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Contact recipient;
        try
        {
            recipient = Contact.Create([(ContactChannel.Email, email)], DateTime.UtcNow);
        }
        catch (ContactValidationException exception)
        {
            throw new ArgumentException(exception.Message, nameof(email), exception);
        }

        try
        {
            return await recipientRepository.CreateAsync(recipient, ct);
        }
        catch (ContactAddressConflictException exception)
        {
            throw new RecipientAlreadyExistsException(exception);
        }
    }

    public Task<PageResult<Contact>> GetRecipientsAsync(int pageNumber, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ct.ThrowIfCancellationRequested();
        return recipientRepository.GetPageAsync(pageNumber, pageSize, ct);
    }

    public Task<PageResult<Campaign>> GetCampaignsAsync(int pageNumber, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ct.ThrowIfCancellationRequested();
        return campaignRepository.GetPage(pageNumber, pageSize, ct);
    }

    public Task<PageResult<Delivery>> GetDeliveriesAsync(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ct.ThrowIfCancellationRequested();
        return deliveryRepository.GetPage(campaignId, pageNumber, pageSize, ct);
    }

    private async Task<Campaign> GetRequiredCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        return await GetCampaignAsync(campaignId, ct)
            ?? throw new KeyNotFoundException($"Кампания {campaignId} не найдена.");
    }
}
