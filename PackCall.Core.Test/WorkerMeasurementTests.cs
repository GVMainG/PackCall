using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PackCall.Core.Diagnostics;
using PackCall.Core.Entities;
using PackCall.Core.Services;
using PackCall.Worker.Background;
using Serilog;

namespace PackCall.Core.Test;

[Collection("Measurement")]
public sealed class WorkerMeasurementTests
{
    [Fact]
    public async Task BatchWaitIncludesCaptureJournalAndPreviousRowsButNotWallClockJump()
    {
        using var collector = new MeasurementCollector(new() { Enabled = true, SessionId = Guid.NewGuid(), OutputDirectory = "unused" });
        var clock = new Clock();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var logger = new LoggerConfiguration().CreateLogger();
        using var worker = new DeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeliveryWorkerOptions()), logger, new WorkerIdentity("worker"), clock);
        var rows = Enumerable.Range(0, 3).Select(_ => Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "synthetic@example.invalid", DateTime.UtcNow)).ToArray();
        foreach (var row in rows) row.SetCapturedForTest("worker", DateTime.UtcNow.AddMinutes(1));
        CaptureTiming.Record(rows, clock);
        clock.Ticks += 100; // capture journal
        var processor = new Processor(clock);
        foreach (var row in rows) await Invoke(worker, processor, new LeaseClock(), row);
        var window = collector.Close()!;
        var wait = window.Histograms[new((int)Operation.BatchWait, Outcome.Success, Purpose.None)];
        Assert.Equal(100, wait.Min);
        Assert.Equal(500, wait.Max);
        Assert.Equal(900, wait.Sum);
        Assert.Equal(3, wait.Count);
        var processing = window.Histograms[new((int)Operation.ProcessingTotal, Outcome.TerminalPathComplete, Purpose.None)];
        Assert.Equal(600, processing.Sum);
        Assert.Equal(3, processor.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LeaseSkipOrInvalidOwnerDoesNotCallService(bool invalidOwner)
    {
        using var collector = new MeasurementCollector(new() { Enabled = true, SessionId = Guid.NewGuid(), OutputDirectory = "unused" });
        var clock = new Clock();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var logger = new LoggerConfiguration().CreateLogger();
        using var worker = new DeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeliveryWorkerOptions()), logger, new WorkerIdentity("worker"), clock);
        var row = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "synthetic@example.invalid", DateTime.UtcNow);
        row.SetCapturedForTest(invalidOwner ? "other" : "worker", DateTime.UtcNow.AddMinutes(-1));
        var processor = new Processor(clock);
        await Invoke(worker, processor, new LeaseClock(), row);
        Assert.Equal(0, processor.Calls);
        var window = collector.Close()!;
        Assert.Equal(1, window.Counters[new((int)Event.ProcessingFinished, invalidOwner ? Outcome.InvalidCapture : Outcome.LeaseSkipped, Purpose.None)].Delta);
    }

    private static Task Invoke(DeliveryWorker worker, IDeliveryProcessingService processor, ILeaseClock clock, Delivery row) =>
        (Task)typeof(DeliveryWorker).GetMethod("ProcessCapturedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, [processor, clock, row, CancellationToken.None])!;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task HostAndLeaseCancellationAreDistinct(bool host)
    {
        using var collector = new MeasurementCollector(new() { Enabled = true, SessionId = Guid.NewGuid(), OutputDirectory = "unused" });
        var clock = new Clock();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var logger = new LoggerConfiguration().CreateLogger();
        using var worker = new DeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeliveryWorkerOptions()), logger, new WorkerIdentity("worker"), clock);
        var row = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "synthetic@example.invalid", DateTime.UtcNow);
        row.SetCapturedForTest("worker", DateTime.UtcNow.AddSeconds(5.2));
        using var cancellation = new CancellationTokenSource();
        if (host) cancellation.Cancel();
        var processor = new Processor(clock) { WaitForCancellation = true };
        var task = (Task)typeof(DeliveryWorker).GetMethod("ProcessCapturedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, [processor, new LeaseClock(), row, cancellation.Token])!;
        if (host) await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        else await task.WaitAsync(TimeSpan.FromSeconds(5));
        var window = collector.Close()!;
        Assert.Equal(1, window.Counters[new((int)Event.Cancellation, host ? Outcome.Host : Outcome.Lease, Purpose.None)].Delta);
    }

    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddHours(Ticks);
    }
    private sealed class LeaseClock : ILeaseClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken ct) => Task.FromResult(DateTime.UtcNow);
    }
    private sealed class Processor(Clock clock) : IDeliveryProcessingService
    {
        public int Calls;
        public bool WaitForCancellation;
        public Task ProcessAsync(Delivery delivery, CancellationToken ct)
        {
            Calls++;
            if (WaitForCancellation) return Task.Delay(Timeout.InfiniteTimeSpan, ct);
            clock.Ticks += 200;
            if (ProcessingObservation.Current is { } observation) observation.Outcome = Outcome.TerminalPathComplete;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<Delivery>> CaptureBatchAsync(int batchSize, string workerId, TimeSpan lockDuration, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ReleaseExpiredLocksAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CompleteCampaignIfFinishedAsync(Guid campaignId, CancellationToken ct) => throw new NotSupportedException();
    }
}
