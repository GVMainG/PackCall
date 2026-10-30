using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Journaling;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;

namespace PackCall.Core.Services;

/// <summary>
/// Сценарий обработки доставок: атомарный захват пакета, передача сообщения
/// mock-оператору, фиксация результата, возврат истёкших закреплений
/// и завершение кампании.
/// </summary>
public sealed class DeliveryProcessingService : IDeliveryProcessingService
{
    private readonly IDeliveryRepository _deliveryRepository;
    private readonly ICampaingRepository _campaignRepository;
    private readonly IMessageSender _messageSender;
    private readonly IDeliveryStatusJournal _journal;
    private readonly SendPolicy _policy;

    public DeliveryProcessingService(
        IDeliveryRepository deliveryRepository,
        ICampaingRepository campaignRepository,
        IMessageSender messageSender,
        IDeliveryStatusJournal journal,
        SendPolicy policy)
    {
        _deliveryRepository = deliveryRepository;
        _campaignRepository = campaignRepository;
        _messageSender = messageSender;
        _journal = journal;
        _policy = policy;
    }

    /// <summary>
    /// Атомарно захватывает не более <paramref name="batchSize"/> доставок со статусом
    /// "Ожидает" и закрепляет их за воркером на <paramref name="lockDuration"/>.
    /// </summary>
    /// <returns>Только успешно захваченные доставки.</returns>
    public async Task<IReadOnlyList<Delivery>> CaptureBatchAsync(
        int batchSize, string workerId, TimeSpan lockDuration, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (lockDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockDuration), "Срок закрепления должен быть положительным.");

        ct.ThrowIfCancellationRequested();

        var captured = await _deliveryRepository.CaptureBatchAsync(batchSize, workerId, lockDuration, ct);

        ct.ThrowIfCancellationRequested();

        foreach (var delivery in captured)
        {
            await _journal.AppendAsync(new StatusChangeEntry(
                delivery.CampaignId,
                delivery.Id,
                DeliveryStatus.Waiting,
                DeliveryStatus.InProgress,
                workerId,
                delivery.UpdatedAt,
                OperatorResult: null), ct);
        }

        return captured;
    }

    /// <summary>
    /// Обрабатывает одну захваченную доставку: передаёт сообщение mock-оператору,
    /// фиксирует ответ и проверяет завершение кампании.
    /// </summary>
    public async Task ProcessAsync(Delivery delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.DeliveryStatus != DeliveryStatus.InProgress)
            throw new DomainException(
                $"Обрабатывать можно только доставку в статусе \"Отправляется\". Текущий статус: {delivery.DeliveryStatus}.");

        ct.ThrowIfCancellationRequested();

        var previousStatus = delivery.DeliveryStatus;
        var workerId = delivery.WorkerId;
        var lockExpiresAt = delivery.LockExpiresAt;
        var campaign = await GetCampaignAsync(delivery.CampaignId, ct);
        if (campaign.Status == CampaignStatus.Paused)
        {
            if (workerId is not null && delivery.LockExpiresAt is { } expiresAt)
            {
                var releasedAt = DateTime.UtcNow;
                var released = await _deliveryRepository.ReleaseCapturedAsync(
                    delivery.Id, workerId, expiresAt, releasedAt, ct);
                if (released is not null)
                {
                    await _journal.AppendAsync(new StatusChangeEntry(
                        delivery.CampaignId, delivery.Id, DeliveryStatus.InProgress, DeliveryStatus.Waiting,
                        workerId, releasedAt, OperatorResult: null), ct);
                }
            }
            return;
        }
        if (campaign.Status != CampaignStatus.InProgress)
            throw new DomainException("Обрабатывать доставки можно только у запущенной кампании.");

        var request = new SendRequest(
            delivery.Id,
            delivery.CampaignId,
            delivery.RecipientId,
            delivery.RecipientEmail,
            campaign.MessageText);

        ct.ThrowIfCancellationRequested();
        var result = await _messageSender.SendAsync(request, _policy.SimulateErrors, ct);

        ct.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        if (result.IsSuccess)
            delivery.StatusFromInProgressToSent(now);
        else
            delivery.StatusFromInProgressToFailed(now);

        if (!await _deliveryRepository.TryCompleteAsync(delivery, workerId, lockExpiresAt, ct))
            return;

        ct.ThrowIfCancellationRequested();

        await _journal.AppendAsync(new StatusChangeEntry(
            delivery.CampaignId,
            delivery.Id,
            previousStatus,
            delivery.DeliveryStatus,
            workerId,
            now,
            OperatorResult: result.Error ?? "AcceptedByProvider"), ct);

        await CompleteCampaignIfFinishedAsync(delivery.CampaignId, ct);
    }

    /// <summary>
    /// Возвращает доставки с истёкшим сроком закрепления из "Отправляется" в "Ожидает".
    /// </summary>
    /// <returns>Количество возвращённых доставок.</returns>
    public async Task<int> ReleaseExpiredLocksAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var released = await _deliveryRepository.ReleaseExpiredLocksAsync(ct);

        ct.ThrowIfCancellationRequested();

        foreach (var delivery in released)
        {
            await _journal.AppendAsync(new StatusChangeEntry(
                delivery.CampaignId,
                delivery.Id,
                DeliveryStatus.InProgress,
                DeliveryStatus.Waiting,
                delivery.WorkerId,
                delivery.UpdatedAt,
                OperatorResult: null), ct);
        }

        return released.Count;
    }

    /// <summary>
    /// Переводит кампанию в "Завершена", если все её доставки получили конечный статус.
    /// </summary>
    /// <returns><see langword="true"/>, если кампания завершена этим вызовом.</returns>
    public async Task<bool> CompleteCampaignIfFinishedAsync(Guid campaignId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var campaigns = await _campaignRepository.Get(campaignId, ct);
        var campaign = campaigns.SingleOrDefault()
            ?? throw new KeyNotFoundException($"Кампания {campaignId} не найдена.");
        if (campaign.Status != CampaignStatus.InProgress)
            return false;

        ct.ThrowIfCancellationRequested();
        if (await _deliveryRepository.HasPendingDeliveriesAsync(campaignId, ct))
            return false;

        ct.ThrowIfCancellationRequested();

        campaign.StatusFromInProgressToCompleted(DateTime.UtcNow);
        return await _campaignRepository.TryUpdateStatusAsync(campaign, CampaignStatus.InProgress, ct);
    }

    private async Task<Campaign> GetCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var campaigns = await _campaignRepository.Get(campaignId, ct);
        return campaigns.SingleOrDefault()
            ?? throw new KeyNotFoundException($"Кампания {campaignId} не найдена.");
    }
}
