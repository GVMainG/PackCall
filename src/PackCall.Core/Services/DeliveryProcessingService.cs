using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Journaling;
using PackCall.Core.Metrics;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;

namespace PackCall.Core.Services;

/// <summary>
/// Сценарий обработки доставок: атомарный захват пакета, передача сообщения
/// mock-оператору, фиксация результата, возврат истёкших закреплений
/// и завершение кампании.
/// </summary>
public sealed class DeliveryProcessingService
{
    private readonly IDeliveryRepository _deliveryRepository;
    private readonly ICampaingRepository _campaignRepository;
    private readonly IMessageSender _messageSender;
    private readonly IDeliveryStatusJournal _journal;
    private readonly IDeliveryMetrics _metrics;

    public DeliveryProcessingService(
        IDeliveryRepository deliveryRepository,
        ICampaingRepository campaignRepository,
        IMessageSender messageSender,
        IDeliveryStatusJournal journal,
        IDeliveryMetrics metrics)
    {
        _deliveryRepository = deliveryRepository;
        _campaignRepository = campaignRepository;
        _messageSender = messageSender;
        _journal = journal;
        _metrics = metrics;
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

        var now = DateTime.UtcNow;
        var captured = await _deliveryRepository.CaptureBatchAsync(batchSize, workerId, lockDuration, now, ct);

        ct.ThrowIfCancellationRequested();

        foreach (var delivery in captured)
        {
            _metrics.RecordStatusChange(DeliveryStatus.Waiting, DeliveryStatus.InProgress);

            await _journal.AppendAsync(new StatusChangeEntry(
                delivery.CampaignId,
                delivery.Id,
                DeliveryStatus.Waiting,
                DeliveryStatus.InProgress,
                workerId,
                now,
                OperatorResult: null), ct);
        }

        _metrics.RecordCaptured(captured.Count);
        return captured;
    }

    /// <summary>
    /// Обрабатывает одну захваченную доставку: передаёт сообщение mock-оператору
    /// с ключом идемпотентности, фиксирует результат и проверяет завершение кампании.
    /// </summary>
    public async Task ProcessAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.DeliveryStatus != DeliveryStatus.InProgress)
            throw new DomainException(
                $"Обрабатывать можно только доставку в статусе \"Отправляется\". Текущий статус: {delivery.DeliveryStatus}.");

        ct.ThrowIfCancellationRequested();

        var previousStatus = delivery.DeliveryStatus;
        var now = DateTime.UtcNow;

        var request = new SendRequest(
            delivery.Id,
            delivery.CampaignId,
            delivery.RecipientId,
            delivery.RecipientEmail,
            await GetMessageTextAsync(delivery, ct));

        var result = await _messageSender.SendAsync(request, ct);

        ct.ThrowIfCancellationRequested();

        if (result.IsSuccess)
            delivery.MarkSent(now);
        else
            delivery.MarkFailed(now);

        await _deliveryRepository.SaveAsync(delivery, ct);

        ct.ThrowIfCancellationRequested();

        _metrics.RecordStatusChange(previousStatus, delivery.DeliveryStatus);
        _metrics.RecordProcessed();
        if (result.IsSuccess)
            _metrics.RecordOperatorSuccess();
        else
            _metrics.RecordOperatorError();
        if (result.IsDuplicateSuppressed)
        {
            _metrics.RecordOperatorRetry();
            _metrics.RecordOperatorDuplicateSuppressed();
        }

        await _journal.AppendAsync(new StatusChangeEntry(
            delivery.CampaignId,
            delivery.Id,
            previousStatus,
            delivery.DeliveryStatus,
            WorkerId: null,
            now,
            OperatorResult: result.Error ?? "Success"), ct);

        await CompleteCampaignIfFinishedAsync(delivery.CampaignId, ct);
    }

    /// <summary>
    /// Возвращает доставки с истёкшим сроком закрепления из "Отправляется" в "Ожидает".
    /// </summary>
    /// <returns>Количество возвращённых доставок.</returns>
    public async Task<int> ReleaseExpiredLocksAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        var released = await _deliveryRepository.ReleaseExpiredLocksAsync(now, ct);

        ct.ThrowIfCancellationRequested();

        foreach (var delivery in released)
        {
            _metrics.RecordStatusChange(DeliveryStatus.InProgress, DeliveryStatus.Waiting);

            await _journal.AppendAsync(new StatusChangeEntry(
                delivery.CampaignId,
                delivery.Id,
                DeliveryStatus.InProgress,
                DeliveryStatus.Waiting,
                delivery.WorkerId,
                now,
                OperatorResult: null), ct);
        }

        _metrics.RecordExpiredLockReturned(released.Count);
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

        campaign.Complete(DateTime.UtcNow);
        await _campaignRepository.Update(campaign, ct);
        return true;
    }

    private async Task<string> GetMessageTextAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.Campaign is not null)
            return delivery.Campaign.MessageText;

        var campaigns = await _campaignRepository.Get(delivery.CampaignId, ct);
        return campaigns.SingleOrDefault()?.MessageText
            ?? throw new KeyNotFoundException($"Кампания {delivery.CampaignId} не найдена.");
    }
}
