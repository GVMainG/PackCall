using PackCall.Core.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core;
using System.Diagnostics;
using PackCall.Core.Entities;
using PackCall.Core.Services;
using ILogger = Serilog.ILogger;

namespace PackCall.Worker.Background;

/// <summary>–ó–∞—Ö–≤–∞—Ç—ã–≤–∞–µ—Ç –∏ –æ–±—Ä–∞–±–∞—Ç—ã–≤–∞–µ—Ç –¥–æ—Å—Ç–∞–≤–∫–∏, –æ—Ç–¥–µ–ª—å–Ω–æ –≤–æ–∑–≤—Ä–∞—â–∞—è –ø—Ä–æ—Å—Ä–æ—á–µ–Ω–Ω—ã–µ –∑–∞–∫—Ä–µ–ø–ª–µ–Ω–∏—è.</summary>
public sealed class DeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeliveryWorkerOptions _options;
    private readonly ILogger _logger;
    private readonly string _workerId;
    private readonly TimeProvider _timeProvider;

    public DeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<DeliveryWorkerOptions> options,
        ILogger logger, WorkerIdentity? identity = null, TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger.ForContext<DeliveryWorker>();

        if (!_options.IsValid())
            throw new OptionsValidationException(DeliveryWorkerOptions.SectionName,
                typeof(DeliveryWorkerOptions),
                ["–ü–∞—Ä–∞–º–µ—Ç—Ä—ã DeliveryWorker –Ω–µ–∫–æ—Ä—Ä–µ–∫—Ç–Ω—ã: Parallelism 1..64, BatchSize –Ω–µ –º–µ–Ω–µ–µ 1, LockDuration –Ω–µ –º–µ–Ω–µ–µ 10 —Å–µ–∫—É–Ω–¥, " +
                 "–≤—Å–µ –∏–Ω—Ç–µ—Ä–≤–∞–ª—ã –ø–æ–ª–æ–∂–∏—Ç–µ–ª—å–Ω—ã, LockDuration + RecoveryInterval –Ω–µ –±–æ–ª–µ–µ 2 –º–∏–Ω—É—Ç."]);

        var host = Environment.MachineName;
        _workerId = identity?.Id ?? $"{host[..Math.Min(host.Length, 200)]}-{Guid.NewGuid():N}";
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("–§–æ–Ω–æ–≤—ã–π –æ–±—Ä–∞–±–æ—Ç—á–∏–∫ –¥–æ—Å—Ç–∞–≤–æ–∫ –∑–∞–ø—É—â–µ–Ω. WorkerId={WorkerId}, Parallelism={Parallelism}, BatchSize={BatchSize}",
            _workerId, _options.Parallelism, _options.BatchSize);

        var processing = Enumerable.Range(0, _options.Parallelism)
            .Select(lane => ProcessLoopAsync(lane, stoppingToken));
        await Task.WhenAll(processing.Append(RecoveryLoopAsync(stoppingToken)));
    }

    private async Task ProcessLoopAsync(int lane, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
                var captured = await processor.CaptureBatchAsync(_options.BatchSize, _workerId, _options.LockDuration, ct);

                if (captured.Count == 0)
                {
                    await Task.Delay(_options.IdleDelay, ct);
                    continue;
                }

                foreach (var delivery in captured)
                {
                    ct.ThrowIfCancellationRequested();
                    await ProcessCapturedAsync(processor, scope.ServiceProvider.GetRequiredService<ILeaseClock>(), delivery, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "–û—à–∏–±–∫–∞ –æ–±—Ä–∞–±–æ—Ç–∫–∏ –¥–æ—Å—Ç–∞–≤–æ–∫. WorkerId={WorkerId}, Lane={Lane}",
                    _workerId, lane);
                await DelayAfterErrorAsync(ct);
            }
        }
    }

    private async Task ProcessCapturedAsync(
        IDeliveryProcessingService processor, ILeaseClock clock, Delivery delivery, CancellationToken ct)
    {
        var start = _timeProvider.GetTimestamp();
        using var observation = PerformanceMetrics.Enabled ? new ProcessingObservation() : null;
        var outcome = Outcome.Error;
        var enteredService = false;
        PerformanceMetrics.Count(Event.ProcessingStarted);
        if (CaptureTiming.TakeWaitMilliseconds(delivery) is { } wait)
            PerformanceMetrics.Duration(Operation.BatchWait, Outcome.Success, wait);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (delivery.WorkerId != _workerId || delivery.LockExpiresAt is not { } expiresAt)
            {
                outcome = Outcome.InvalidCapture;
                _logger.Warning("«‡ı‚‡˜ÂÌÌ‡ˇ ‰ÓÒÚ‡‚Í‡ {DeliveryId} ÌÂ Á‡ÍÂÔÎÂÌ‡ Á‡ ‚ÓÍÂÓÏ {WorkerId}", delivery.Id, _workerId);
                return;
            }

            var safetyMargin = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(5).Ticks,
                _options.LockDuration.Ticks / 10));
            var remaining = await MeasuredOperation.Run(Operation.LeaseCheck, async () =>
            {
                var started = _timeProvider.GetTimestamp();
                var dbNow = await clock.GetUtcNowAsync(ct);
                ct.ThrowIfCancellationRequested();
                return expiresAt - dbNow - _timeProvider.GetElapsedTime(started) - safetyMargin;
            }, value => value <= TimeSpan.Zero ? Outcome.LeaseSkipped : Outcome.Success, time: _timeProvider);
            if (remaining <= TimeSpan.Zero)
            {
                outcome = Outcome.LeaseSkipped;
                _logger.Warning("«‡ÍÂÔÎÂÌËÂ ‰ÓÒÚ‡‚ÍË {DeliveryId} ËÒÚÂÍ‡ÂÚ ‰Ó Ì‡˜‡Î‡ Ó·‡·ÓÚÍË", delivery.Id);
                return;
            }

            using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            leaseCancellation.CancelAfter(remaining);
            try
            {
                enteredService = true;
                await processor.ProcessAsync(delivery, leaseCancellation.Token);
                outcome = observation?.Outcome ?? Outcome.Success;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && leaseCancellation.IsCancellationRequested)
            {
                outcome = Outcome.Cancelled;
                PerformanceMetrics.Count(Event.Cancellation, Outcome.Lease);
                _logger.Warning("¬ÂÏˇ Á‡ÍÂÔÎÂÌËˇ ‰ÓÒÚ‡‚ÍË {DeliveryId} ËÒÚÂÍ‡ÂÚ; ‰ÓÒÚ‡‚Í‡ ·Û‰ÂÚ ‚ÓÁ‚‡˘ÂÌ‡ ‰Îˇ ÔÓ‚ÚÓ‡", delivery.Id);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = Outcome.Cancelled;
            PerformanceMetrics.Count(Event.Cancellation, ct.IsCancellationRequested ? Outcome.Host : Outcome.Other);
            throw;
        }
        finally
        {
            if (!enteredService) PerformanceMetrics.Count(Event.ProcessingFinished, outcome);
            PerformanceMetrics.Duration(Operation.ProcessingTotal, outcome, _timeProvider.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    private async Task RecoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
                var released = await processor.ReleaseExpiredLocksAsync(ct);
                if (released > 0)
                    _logger.Information("–í–æ–∑–≤—Ä–∞—â–µ–Ω–æ –ø—Ä–æ—Å—Ä–æ—á–µ–Ω–Ω—ã—Ö –¥–æ—Å—Ç–∞–≤–æ–∫: {Count}", released);

                await Task.Delay(_options.RecoveryInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "–û—à–∏–±–∫–∞ –≤–æ–∑–≤—Ä–∞—Ç–∞ –ø—Ä–æ—Å—Ä–æ—á–µ–Ω–Ω—ã—Ö –¥–æ—Å—Ç–∞–≤–æ–∫");
                await DelayAfterErrorAsync(ct);
            }
        }
    }

    private async Task DelayAfterErrorAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_options.ErrorDelay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
