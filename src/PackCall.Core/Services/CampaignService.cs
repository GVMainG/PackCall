using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;
using PackCall.Core.Exceptions;

namespace PackCall.Core.Services;

// Управляет жизненным циклом кампании и формирует очередь доставок для её аудитории.
public sealed class CampaignService : ICampaignService
{
    private const int FormationBatchSize = 500;
    private readonly ICampaingRepository _campaignRepository;
    private readonly IDeliveryRepository _deliveryRepository;
    private readonly IContactRepository _recipientRepository;
    private readonly IChannelOperatorAvailability _operators;

    public CampaignService(
        ICampaingRepository campaignRepository,
        IDeliveryRepository deliveryRepository,
        IContactRepository recipientRepository,
        IChannelOperatorAvailability operators)
    {
        _campaignRepository = campaignRepository;
        _deliveryRepository = deliveryRepository;
        _recipientRepository = recipientRepository;
        _operators = operators;
    }

    public async Task<Guid> CreateCampaignSnapshotAsync(string name, string messageText,
        IReadOnlyList<Guid>? contactIds, IReadOnlyList<string?> channels, CancellationToken ct) =>
        (await SaveCampaignSnapshotAsync(name, messageText, contactIds, channels, ct)).Id;

    public async Task<Guid> CreateCampaignAsync(string name, string messageText,
        IReadOnlyList<Guid>? contactIds, IReadOnlyList<string?> channels, CancellationToken ct)
    {
        Campaign campaign;
        try
        {
            campaign = await SaveCampaignSnapshotAsync(name, messageText, contactIds, channels, ct);
        }
        catch (Exception error) when (error is not (ArgumentException or OperationCanceledException))
        {
            throw new CampaignFormationException(error);
        }
        return await CompleteFormationAsync(campaign, () => FormDeliveriesFromSnapshotAsync(campaign.Id, ct), ct);
    }

    private async Task<Campaign> SaveCampaignSnapshotAsync(string name, string messageText,
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

        var campaign = Campaign.Create(name, messageText);
        campaign.StatusFromDraftToForming();
        await _campaignRepository.CreateWithSnapshotAsync(campaign, selectedContacts, channelSnapshot, ct);
        return campaign;
    }

    public Task<int> FormDeliveriesFromSnapshotAsync(Guid campaignId, CancellationToken ct) =>
        _deliveryRepository.FormDeliveriesFromSnapshotAsync(campaignId, ct);

    public async Task<Guid> CreateCampaignAsync(string name, string messageText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var campaign = Campaign.Create(name, messageText);
        // Кампанию можно запустить только после записи последнего пакета доставок.
        campaign.StatusFromDraftToForming();
        try
        {
            await _campaignRepository.Create(campaign, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new CampaignFormationException(error);
        }

        return await CompleteFormationAsync(campaign, () => FormLegacyEmailDeliveriesAsync(campaign, ct), ct);
    }

    private async Task<Guid> CompleteFormationAsync(Campaign campaign, Func<Task> formDeliveries, CancellationToken ct)
    {
        try
        {
            await formDeliveries();
            ct.ThrowIfCancellationRequested();
            campaign.StatusFromFormingToDraft();
            if (!await _campaignRepository.TryUpdateStatusAsync(campaign, CampaignStatus.Forming, ct))
                throw new InvalidOperationException("Не удалось перевести формируемую кампанию в черновик.");
            return campaign.Id;
        }
        catch (Exception creationError)
        {
            try
            {
                // Отмена запроса не должна прерывать удаление уже сохранённой части кампании.
                await _campaignRepository.DeleteIfFormingAsync(campaign.Id, CancellationToken.None);
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
            var recipients = await _recipientRepository.GetBatchAsync(offset, FormationBatchSize, ct);
            if (recipients.Count == 0)
                break;

            var now = DateTime.UtcNow;
            var deliveries = recipients.Select(recipient => Delivery.Create(campaign, recipient, now)).ToList();
            ct.ThrowIfCancellationRequested();
            await _deliveryRepository.AddLegacyEmailBatchAsync(deliveries, ct);
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
        if (previousStatus == CampaignStatus.Draft)
        {
            var channels = await _campaignRepository.GetChannelsAsync(campaignId, ct);
            if (channels.Count == 0)
                channels = [ContactChannel.Email];

            foreach (var channel in channels)
                if (!_operators.HasOperator(channel))
                    throw new ChannelOperatorUnavailableException(channel);
        }
        ct.ThrowIfCancellationRequested();
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

        // Если последние отправки завершились на паузе, после возобновления воркеру уже нечего захватывать.
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

        // При гонке считаем переход успешным, если другой запрос уже установил нужный статус.
        var current = await GetRequiredCampaignAsync(campaign.Id, ct);
        if (current.Status != campaign.Status)
            throw new DomainException("Статус кампании изменился другим запросом. Повторите операцию.");
    }

    public async Task<Campaign?> GetCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await _campaignRepository.Get(campaignId, ct);
    }

    public Task<CampaignDetails?> GetCampaignDetailsAsync(Guid campaignId, CancellationToken ct) =>
        _campaignRepository.GetDetailsAsync(campaignId, ct);

    public async Task<Delivery?> GetDeliveryAsync(Guid deliveryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return await _deliveryRepository.Get(deliveryId, ct);
    }

    public Task<DeliveryStatusCounts> GetDeliveryStatusCountsAsync(Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _deliveryRepository.GetStatusCountsAsync(campaignId, ct);
    }

    public Task<Contact?> GetRecipientAsync(Guid recipientId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return _recipientRepository.GetAsync(recipientId, ct);
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
            return await _recipientRepository.CreateAsync(recipient, ct);
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

    private async Task<Campaign> GetRequiredCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        return await GetCampaignAsync(campaignId, ct)
            ?? throw new KeyNotFoundException($"Кампания {campaignId} не найдена.");
    }
}
