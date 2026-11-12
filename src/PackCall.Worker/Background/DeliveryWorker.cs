using PackCall.Core.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core;
using PackCall.Core.Entities;
using PackCall.Core.Services;
using ILogger = Serilog.ILogger;

namespace PackCall.Worker.Background;

/// <summary>Захватывает и обрабатывает доставки, отдельно возвращая просроченные закрепления.</summary>
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
                ["Параметры DeliveryWorker некорректны: Parallelism 1..64, BatchSize не менее 1, LockDuration не менее 10 секунд, " +
                 "все интервалы положительны, LockDuration + RecoveryInterval не более 2 минут."]);

        _workerId = identity?.Id ?? WorkerIdentity.CreateId();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("Фоновый обработчик доставок запущен. WorkerId={WorkerId}, Parallelism={Parallelism}, BatchSize={BatchSize}",
            _workerId, _options.Parallelism, _options.BatchSize);

        var processing = Enumerable.Range(0, _options.Parallelism)
            .Select(lane => RunLoopAsync(lane, stoppingToken));
        await Task.WhenAll(processing.Append(RunLoopAsync(processingLane: null, stoppingToken)));
    }

    private async Task RunLoopAsync(int? processingLane, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
                if (processingLane is null)
                {
                    var released = await processor.ReleaseExpiredLocksAsync(ct);
                    if (released > 0)
                        _logger.Information("Возвращено просроченных доставок: {Count}", released);

                    await Task.Delay(_options.RecoveryInterval, ct);
                    continue;
                }

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
                if (processingLane is { } lane)
                    _logger.Error(exception, "Ошибка обработки доставок. WorkerId={WorkerId}, Lane={Lane}",
                        _workerId, lane);
                else
                    _logger.Error(exception, "Ошибка возврата просроченных доставок");

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
                _logger.Warning("Захваченная доставка {DeliveryId} не закреплена за воркером {WorkerId}", delivery.Id, _workerId);
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
                _logger.Warning("Закрепление доставки {DeliveryId} истекает до начала обработки", delivery.Id);
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
                _logger.Warning("Время закрепления доставки {DeliveryId} истекает; доставка будет возвращена для повтора", delivery.Id);
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
