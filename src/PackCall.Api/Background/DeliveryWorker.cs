using Microsoft.Extensions.Options;
using PackCall.Core.Entities;
using PackCall.Core.Services;
using Serilog;

namespace PackCall.Api.Background;

/// <summary>Захватывает и обрабатывает доставки, отдельно возвращая просроченные закрепления.</summary>
public sealed class DeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeliveryWorkerOptions _options;
    private readonly Serilog.ILogger _logger;
    private readonly string _workerId;

    public DeliveryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<DeliveryWorkerOptions> options,
        Serilog.ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger.ForContext<DeliveryWorker>();

        if (!_options.IsValid())
            throw new OptionsValidationException(DeliveryWorkerOptions.SectionName,
                typeof(DeliveryWorkerOptions),
                ["Параметры DeliveryWorker некорректны: Parallelism 1..64, BatchSize не менее 1, LockDuration не менее 10 секунд, " +
                 "все интервалы положительны, LockDuration + RecoveryInterval не более 2 минут."]);

        var host = Environment.MachineName;
        _workerId = $"{host[..Math.Min(host.Length, 200)]}-{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("Фоновый обработчик доставок запущен. WorkerId={WorkerId}, Parallelism={Parallelism}, BatchSize={BatchSize}",
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
                    await ProcessCapturedAsync(processor, delivery, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Ошибка обработки доставок. WorkerId={WorkerId}, Lane={Lane}",
                    _workerId, lane);
                await DelayAfterErrorAsync(ct);
            }
        }
    }

    private async Task ProcessCapturedAsync(
        IDeliveryProcessingService processor, Delivery delivery, CancellationToken ct)
    {
        if (delivery.WorkerId != _workerId || delivery.LockExpiresAt is not { } expiresAt)
        {
            _logger.Warning("Захваченная доставка {DeliveryId} не закреплена за воркером {WorkerId}",
                delivery.Id, _workerId);
            return;
        }

        var safetyMargin = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(5).Ticks,
            _options.LockDuration.Ticks / 10));
        var remaining = expiresAt - DateTime.UtcNow - safetyMargin;
        if (remaining <= TimeSpan.Zero)
        {
            _logger.Warning("Закрепление доставки {DeliveryId} истекает до начала обработки", delivery.Id);
            return;
        }

        using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        leaseCancellation.CancelAfter(remaining);
        try
        {
            await processor.ProcessAsync(delivery, leaseCancellation.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && leaseCancellation.IsCancellationRequested)
        {
            _logger.Warning("Время закрепления доставки {DeliveryId} истекает; доставка будет возвращена для повтора",
                delivery.Id);
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
                    _logger.Information("Возвращено просроченных доставок: {Count}", released);

                await Task.Delay(_options.RecoveryInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Ошибка возврата просроченных доставок");
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
