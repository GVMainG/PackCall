using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PackCall.Core;
using PackCall.Core.Entities;
using PackCall.Core.Services;
using PackCall.Worker.Background;
using Serilog;

namespace PackCall.Core.Test;

public class WorkerLeaseBudgetTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("expired", false)]
    [InlineData("elapsed", false)]
    [InlineData("error", false)]
    public async Task Budget_UsesDatabaseClockAndElapsed_WithoutChangingBatchExpiry(string mode, bool expectedSend)
    {
        // Deliberately far from application UTC: local wall clock must not decide the budget.
        var dbNow = new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var processor = new Processor(dbNow.AddSeconds(mode == "expired" ? -1 : 10));
        var time = new ControlledTime();
        var clock = new Clock(() =>
        {
            if (mode == "error") throw new InvalidOperationException("clock unavailable");
            if (mode == "elapsed") time.Elapsed += TimeSpan.FromSeconds(10);
            return dbNow;
        });
        using var services = new ServiceCollection().AddSingleton<IDeliveryProcessingService>(processor)
            .AddSingleton<ILeaseClock>(clock).BuildServiceProvider();
        using var logger = new LoggerConfiguration().CreateLogger();
        using var worker = new DeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeliveryWorkerOptions { Parallelism = 1, BatchSize = 2,
                LockDuration = TimeSpan.FromSeconds(10), IdleDelay = TimeSpan.FromMilliseconds(5),
                ErrorDelay = TimeSpan.FromMilliseconds(5), RecoveryInterval = TimeSpan.FromMilliseconds(5) }),
            logger, timeProvider: time);
        await worker.StartAsync(CancellationToken.None);
        try { await processor.NextCapture.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.Equal(expectedSend ? 2 : 0, processor.Sent.Count);
        Assert.All(processor.Sent, d => Assert.Equal(processor.Expiry, d.LockExpiresAt));
        Assert.All(processor.Batch, d => Assert.Equal(DeliveryStatus.InProgress, d.DeliveryStatus));
    }

    [Fact]
    public async Task Budget_CancelsActiveProcessing_ThenContinuesLaneWithoutChangingLease()
    {
        var dbNow = new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Valid ten-second options give a one-second safety margin, leaving 50ms.
        var processor = new Processor(dbNow.AddMilliseconds(1050));
        var nextProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = false;
        processor.Handler = async (delivery, ct) =>
        {
            if (delivery.Id == processor.Batch[0].Id)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException)
                {
                    firstCancelled = ct.IsCancellationRequested;
                    throw;
                }
            }
            else
            {
                Assert.False(ct.IsCancellationRequested);
                nextProcessed.TrySetResult();
            }
        };
        using var services = new ServiceCollection().AddSingleton<IDeliveryProcessingService>(processor)
            .AddSingleton<ILeaseClock>(new Clock(() => dbNow)).BuildServiceProvider();
        using var logger = new LoggerConfiguration().CreateLogger();
        using var worker = new DeliveryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeliveryWorkerOptions { Parallelism = 1, BatchSize = 2,
                LockDuration = TimeSpan.FromSeconds(10), IdleDelay = TimeSpan.FromMilliseconds(5),
                ErrorDelay = TimeSpan.FromMilliseconds(5), RecoveryInterval = TimeSpan.FromMilliseconds(5) }),
            logger, timeProvider: new ControlledTime());
        await worker.StartAsync(CancellationToken.None);
        try { await nextProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.True(firstCancelled);
        Assert.Equal(processor.Batch.Select(d => d.Id), processor.Sent.Select(d => d.Id));
        Assert.All(processor.Batch, d =>
        {
            Assert.Equal(processor.Expiry, d.LockExpiresAt);
            Assert.Equal(DeliveryStatus.InProgress, d.DeliveryStatus);
            Assert.Null(d.CompletedAt);
        });
    }

    private sealed class ControlledTime : TimeProvider
    {
        public TimeSpan Elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }
    private sealed class Clock(Func<DateTime> read) : ILeaseClock
    {
        public Task<DateTime> GetUtcNowAsync(CancellationToken ct) => Task.FromResult(read());
    }
    private sealed class Processor(DateTime expiry) : IDeliveryProcessingService
    {
        private int _capture;
        public DateTime Expiry => expiry;
        public List<Delivery> Sent { get; } = [];
        public List<Delivery> Batch { get; } = [];
        public TaskCompletionSource NextCapture { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<Delivery>> CaptureBatchAsync(int size, string owner, TimeSpan duration, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _capture) > 1)
            {
                NextCapture.TrySetResult();
                return Task.FromResult<IReadOnlyList<Delivery>>([]);
            }
            for (var i = 0; i < 2; i++)
            {
                var d = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "user@example.com", expiry.AddSeconds(-10));
                d.SetCapturedForTest(owner, expiry, expiry.AddSeconds(-10));
                Batch.Add(d);
            }
            return Task.FromResult<IReadOnlyList<Delivery>>(Batch);
        }
        public Func<Delivery, CancellationToken, Task>? Handler { get; set; }
        public Task ProcessAsync(Delivery delivery, CancellationToken ct)
        {
            Sent.Add(delivery);
            return Handler?.Invoke(delivery, ct) ?? Task.CompletedTask;
        }
        public Task<int> ReleaseExpiredLocksAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<bool> CompleteCampaignIfFinishedAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
    }
}
