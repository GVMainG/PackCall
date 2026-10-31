using PackCall.Core.Diagnostics;
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
    private readonly TimeProvider _time;

    public DeliveryProcessingService(
        IDeliveryRepository deliveryRepository,
        ICampaingRepository campaignRepository,
        IMessageSender messageSender,
        IDeliveryStatusJournal journal,
        SendPolicy policy, TimeProvider? timeProvider = null)
    {
        _deliveryRepository = deliveryRepository;
        _campaignRepository = campaignRepository;
        _messageSender = messageSender;
        _journal = journal;
        _policy = policy;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Атомарно захватывает не более <paramref name="batchSize"/> доставок со статусом
    /// "Ожидает" и закрепляет их за воркером на <paramref name="lockDuration"/>.
    /// </summary>
    /// <returns>Только успешно захваченные доставки.</returns>
    public Task<IReadOnlyList<Delivery>> CaptureBatchAsync(
        int batchSize, string workerId, TimeSpan lockDuration, CancellationToken ct)
    {
        PerformanceMetrics.Count(Event.CaptureCalls);
        return MeasuredOperation.Run(Operation.CaptureTotal,
            () => CaptureCoreAsync(batchSize, workerId, lockDuration, ct), time: _time);
    }

    private async Task<IReadOnlyList<Delivery>> CaptureCoreAsync(
        int batchSize, string workerId, TimeSpan lockDuration, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (lockDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lockDuration), "Срок закрепления должен быть положительным.");

        ct.ThrowIfCancellationRequested();

        var captured = await MeasuredOperation.Run(Operation.CaptureStorage,
            () => _deliveryRepository.CaptureBatchAsync(batchSize, workerId, lockDuration, ct), time: _time);
        CaptureTiming.Record(captured, _time);
        PerformanceMetrics.Count(Event.CaptureRows, value: captured.Count);
        if (captured.Count == 0) PerformanceMetrics.Count(Event.CaptureEmpty);

        ct.ThrowIfCancellationRequested();

        foreach (var delivery in captured)
        {
            await AppendMeasuredAsync(new StatusChangeEntry(
                delivery.CampaignId,
                delivery.Id,
                DeliveryStatus.Waiting,
                DeliveryStatus.InProgress,
                workerId,
                delivery.UpdatedAt,
                OperatorResult: null), ct, Purpose.Capture);
        }

        return captured;
    }

    /// <summary>
    /// Обрабатывает одну захваченную доставку: передаёт сообщение mock-оператору,
    /// фиксирует ответ и проверяет завершение кампании.
    /// </summary>
    public async Task ProcessAsync(Delivery delivery, CancellationToken ct)
    {
        var attempt = new Attempt();
        PerformanceMetrics.Count(Event.ServiceStarted);
        try
        {
            await ProcessCoreAsync(delivery, ct, attempt);
            if (ProcessingObservation.Current is { } observation) observation.Outcome = attempt.Outcome;
            PerformanceMetrics.Count(Event.ProcessingFinished, attempt.Outcome);
        }
        catch (Exception exception)
        {
            var outcome = exception is OperationCanceledException ? Outcome.Cancelled : Outcome.Error;
            if (ProcessingObservation.Current is { } observation) observation.Outcome = outcome;
            PerformanceMetrics.Count(Event.ProcessingFinished, outcome);
            if (attempt.Applied) PerformanceMetrics.Count(Event.AfterTerminalApplied, outcome);
            throw;
        }
    }

    private sealed class Attempt
    {
        public bool Applied;
        public Outcome Outcome = Outcome.TerminalPathComplete;
    }

    private async Task ProcessCoreAsync(Delivery delivery, CancellationToken ct, Attempt attempt)
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
            attempt.Outcome = Outcome.PausedNoRelease;
            if (workerId is not null && delivery.LockExpiresAt is { } expiresAt)
            {
                var releasedAt = DateTime.UtcNow;
                var released = await _deliveryRepository.ReleaseCapturedAsync(
                    delivery.Id, workerId, expiresAt, releasedAt, ct);
                if (released is not null)
                {
                    attempt.Outcome = Outcome.PausedRelease;
                    await AppendMeasuredAsync(new StatusChangeEntry(
                        delivery.CampaignId, delivery.Id, DeliveryStatus.InProgress, DeliveryStatus.Waiting,
                        workerId, releasedAt, OperatorResult: null), ct, Purpose.Pause);
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
        var result = await MeasuredOperation.Run(Operation.OperatorCall,
            () => _messageSender.SendAsync(request, _policy.SimulateErrors, ct),
            value => value.IsSuccess ? Outcome.Accepted : Outcome.Rejected, Event.OperatorFinished, time: _time);

        ct.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        if (result.IsSuccess)
            delivery.StatusFromInProgressToSent(now);
        else
            delivery.StatusFromInProgressToFailed(now);

        if (!await MeasuredOperation.Run(Operation.ResultPersist,
            () => _deliveryRepository.TryCompleteAsync(delivery, workerId, lockExpiresAt, ct),
            value => value ? Outcome.Applied : Outcome.NotApplied, Event.ResultFinished, time: _time))
        {
            attempt.Outcome = Outcome.StaleResult;
            return;
        }
        attempt.Applied = true;
        PerformanceMetrics.Count(Event.TerminalApplied,
            delivery.DeliveryStatus == DeliveryStatus.Sent ? Outcome.Sent : Outcome.Failed);

        ct.ThrowIfCancellationRequested();

        await AppendMeasuredAsync(new StatusChangeEntry(
            delivery.CampaignId,
            delivery.Id,
            previousStatus,
            delivery.DeliveryStatus,
            workerId,
            now,
            OperatorResult: result.Error ?? "AcceptedByProvider"), ct, Purpose.Terminal);

        await CompleteCampaignIfFinishedAsync(delivery.CampaignId, ct);
    }

    /// <summary>
    /// Возвращает доставки с истёкшим сроком закрепления из "Отправляется" в "Ожидает".
    /// </summary>
    /// <returns>Количество возвращённых доставок.</returns>
    public Task<int> ReleaseExpiredLocksAsync(CancellationToken ct)
    {
        PerformanceMetrics.Count(Event.RecoveryCalls);
        return MeasuredOperation.Run(Operation.RecoveryTotal, () => ReleaseExpiredCoreAsync(ct), time: _time);
    }

    private async Task<int> ReleaseExpiredCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var released = await MeasuredOperation.Run(Operation.RecoveryStorage,
            () => _deliveryRepository.ReleaseExpiredLocksAsync(ct), time: _time);
        PerformanceMetrics.Count(Event.RecoveryRows, value: released.Count);

        ct.ThrowIfCancellationRequested();

        foreach (var delivery in released)
        {
            await AppendMeasuredAsync(new StatusChangeEntry(
                delivery.CampaignId,
                delivery.Id,
                DeliveryStatus.InProgress,
                DeliveryStatus.Waiting,
                delivery.WorkerId,
                delivery.UpdatedAt,
                OperatorResult: null), ct, Purpose.Recovery);
        }

        return released.Count;
    }

    /// <summary>
    /// Переводит кампанию в "Завершена", если все её доставки получили конечный статус.
    /// </summary>
    /// <returns><see langword="true"/>, если кампания завершена этим вызовом.</returns>
    public Task<bool> CompleteCampaignIfFinishedAsync(Guid campaignId, CancellationToken ct)
    {
        PerformanceMetrics.Count(Event.CampaignChecks);
        return MeasuredOperation.Run(Operation.CampaignCheck, () => CompleteCampaignCoreAsync(campaignId, ct), time: _time);
    }

    private async Task<bool> CompleteCampaignCoreAsync(Guid campaignId, CancellationToken ct)
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
        var applied = await MeasuredOperation.Run(Operation.CampaignPersist,
            () => _campaignRepository.TryUpdateStatusAsync(campaign, CampaignStatus.InProgress, ct),
            value => value ? Outcome.Applied : Outcome.NotApplied, time: _time);
        if (applied) PerformanceMetrics.Count(Event.CampaignCompleted);
        return applied;
    }

    private Task AppendMeasuredAsync(StatusChangeEntry entry, CancellationToken ct, Purpose purpose) =>
        MeasuredOperation.Run(Operation.JournalAppend, () => _journal.AppendAsync(entry, ct),
            Event.JournalFinished, purpose, _time);

    private async Task<Campaign> GetCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        var campaigns = await _campaignRepository.Get(campaignId, ct);
        return campaigns.SingleOrDefault()
            ?? throw new KeyNotFoundException($"Кампания {campaignId} не найдена.");
    }
}
