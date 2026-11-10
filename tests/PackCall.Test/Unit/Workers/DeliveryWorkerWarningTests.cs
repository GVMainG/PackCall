using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PackCall.Core.Diagnostics;
using PackCall.Core.Entities;
using PackCall.Core.Services;
using PackCall.Worker.Background;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PackCall.Core.Test;

// Pins the operator-facing Russian warning text produced by DeliveryWorker. The
// RUN-6 acceptance script waits for the lease-cancellation marker
// "истекает; доставка" in the worker log, so a valid-UTF-8 but wrong re-encode
// must fail here instead of silently breaking acceptance.
[Collection("Measurement")]
[Trait("Category", "Unit")]
[Trait("Module", "Workers")]
public sealed class DeliveryWorkerWarningTests
{
    [Fact]
    public async Task InvalidCaptureWarningIsReadableRussian()
    {
        var sink = new CaptureSink();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = CreateWorker(services, sink);
        var row = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "synthetic@example.invalid", DateTime.UtcNow);
        row.SetCapturedForTest("other", DateTime.UtcNow.AddMinutes(1));

        await Invoke(worker, new Processor(), new LeaseClock(), row, CancellationToken.None);

        Assert.Contains(sink.Messages, message =>
            message.Contains("Захваченная доставка") && message.Contains("не закреплена за воркером"));
    }

    [Fact]
    public async Task ExpiredLeaseWarningIsReadableRussian()
    {
        var sink = new CaptureSink();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = CreateWorker(services, sink);
        var row = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "synthetic@example.invalid", DateTime.UtcNow);
        row.SetCapturedForTest("worker", DateTime.UtcNow.AddMinutes(-1));

        await Invoke(worker, new Processor(), new LeaseClock(), row, CancellationToken.None);

        Assert.Contains(sink.Messages, message =>
            message.Contains("Закрепление доставки") && message.Contains("истекает до начала обработки"));
    }

    [Fact]
    public async Task LeaseCancellationWarningIsReadableRussian()
    {
        var sink = new CaptureSink();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var worker = CreateWorker(services, sink);
        var row = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "synthetic@example.invalid", DateTime.UtcNow);
        row.SetCapturedForTest("worker", DateTime.UtcNow.AddSeconds(5.2));

        await Invoke(worker, new Processor { WaitForCancellation = true }, new LeaseClock(), row, CancellationToken.None);

        Assert.Contains(sink.Messages, message =>
            message.Contains("Время закрепления доставки") && message.Contains("истекает; доставка"));
    }

    private static DeliveryWorker CreateWorker(IServiceProvider services, CaptureSink sink) =>
        new(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeliveryWorkerOptions()),
            new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger(),
            new WorkerIdentity("worker"),
            new Clock());

    private static Task Invoke(
        DeliveryWorker worker, IDeliveryProcessingService processor, ILeaseClock clock, Delivery row, CancellationToken token) =>
        (Task)typeof(DeliveryWorker).GetMethod("ProcessCapturedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, [processor, clock, row, token])!;

    private sealed class CaptureSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_events) return _events.Select(entry => entry.RenderMessage()).ToArray();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => 0;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private sealed class LeaseClock : ILeaseClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken ct) => Task.FromResult(DateTime.UtcNow);
    }

    private sealed class Processor : IDeliveryProcessingService
    {
        public bool WaitForCancellation;

        public Task ProcessAsync(Delivery delivery, CancellationToken ct)
        {
            if (WaitForCancellation) return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (ProcessingObservation.Current is { } observation) observation.Outcome = Outcome.TerminalPathComplete;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Delivery>> CaptureBatchAsync(int batchSize, string workerId, TimeSpan lockDuration, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ReleaseExpiredLocksAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CompleteCampaignIfFinishedAsync(Guid campaignId, CancellationToken ct) => throw new NotSupportedException();
    }
}
