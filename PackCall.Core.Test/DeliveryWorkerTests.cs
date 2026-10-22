using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PackCall.Api.Background;
using PackCall.Core.Entities;
using PackCall.Core.Services;
using Serilog;

namespace PackCall.Core.Test;

public class DeliveryWorkerTests
{
    [Fact]
    public async Task Worker_CapturesProcessesAndRecoversWithAnAssignedId()
    {
        var processor = new RecordingProcessor();
        using var provider = new ServiceCollection()
            .AddSingleton(processor)
            .AddScoped<IDeliveryProcessingService>(services => services.GetRequiredService<RecordingProcessor>())
            .BuildServiceProvider();
        var options = Options.Create(new DeliveryWorkerOptions
        {
            Parallelism = 1,
            BatchSize = 1,
            LockDuration = TimeSpan.FromSeconds(10),
            IdleDelay = TimeSpan.FromMilliseconds(10),
            RecoveryInterval = TimeSpan.FromMilliseconds(10),
            ErrorDelay = TimeSpan.FromMilliseconds(10)
        });
        using var logger = new LoggerConfiguration().CreateLogger();
        using var worker = new DeliveryWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            options, logger);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var capturedWorkerId = await processor.Processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await processor.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(string.IsNullOrWhiteSpace(capturedWorkerId));
            Assert.Equal(1, processor.RequestedBatchSize);
            Assert.Equal(DeliveryStatus.InProgress, processor.ProcessedStatus);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 3)]
    [InlineData(3, 1)]
    public async Task Worker_ProcessesJsonConfiguredBatchesWithLimitedParallelism(int parallelism, int batchSize)
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes($$"""
            { "DeliveryWorker": { "Parallelism": {{parallelism}}, "BatchSize": {{batchSize}} } }
            """));
        using var configuration = new ConfigurationManager();
        configuration.AddJsonStream(json);
        var processor = new BatchProcessor(parallelism, batchSize);
        using var provider = new ServiceCollection()
            .Configure<DeliveryWorkerOptions>(configuration.GetSection(DeliveryWorkerOptions.SectionName))
            .AddSingleton<IDeliveryProcessingService>(processor)
            .BuildServiceProvider();
        using var logger = new LoggerConfiguration().CreateLogger();
        using var worker = new DeliveryWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<DeliveryWorkerOptions>>(), logger);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await processor.AllLanesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(parallelism * batchSize, processor.Captured.Count);

            processor.ContinueProcessing.TrySetResult();
            await processor.AllProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(parallelism * batchSize + 1, processor.ProcessedIds.Count);
            Assert.Equal(processor.Captured.Select(delivery => delivery.Id).Order(), processor.ProcessedIds.Order());
            Assert.All(processor.RequestedBatchSizes, size => Assert.Equal(batchSize, size));
            Assert.All(processor.ConcurrentProcessingCounts, count => Assert.InRange(count, 1, parallelism));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Worker_RejectsNonPositiveBatchSize(int batchSize)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var logger = new LoggerConfiguration().CreateLogger();

        Assert.Throws<OptionsValidationException>(() => new DeliveryWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeliveryWorkerOptions { BatchSize = batchSize }), logger));
    }

    private sealed class BatchProcessor(int parallelism, int batchSize) : IDeliveryProcessingService
    {
        private int _batches;
        private int _started;
        private int _active;
        private int _processed;

        public ConcurrentBag<Delivery> Captured { get; } = [];
        public ConcurrentBag<Guid> ProcessedIds { get; } = [];
        public ConcurrentBag<int> RequestedBatchSizes { get; } = [];
        public ConcurrentBag<int> ConcurrentProcessingCounts { get; } = [];
        public TaskCompletionSource AllLanesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueProcessing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllProcessed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<Delivery>> CaptureBatchAsync(
            int requestedBatchSize, string workerId, TimeSpan lockDuration, CancellationToken ct)
        {
            RequestedBatchSizes.Add(requestedBatchSize);
            var batch = Interlocked.Increment(ref _batches);
            var count = batch <= parallelism ? requestedBatchSize : batch == parallelism + 1 ? 1 : 0;
            var deliveries = new List<Delivery>();
            var now = DateTime.UtcNow;
            for (var i = 0; i < count; i++)
            {
                var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "user@example.com", now);
                delivery.StatusFromWaitingToInProgress(workerId, now + lockDuration, now);
                deliveries.Add(delivery);
                Captured.Add(delivery);
            }

            return Task.FromResult<IReadOnlyList<Delivery>>(deliveries);
        }

        public async Task ProcessAsync(Delivery delivery, CancellationToken ct)
        {
            ConcurrentProcessingCounts.Add(Interlocked.Increment(ref _active));
            try
            {
                if (Interlocked.Increment(ref _started) == parallelism)
                    AllLanesStarted.TrySetResult();

                await ContinueProcessing.Task.WaitAsync(ct);
                await Task.Yield();
                ProcessedIds.Add(delivery.Id);
                if (Interlocked.Increment(ref _processed) == parallelism * batchSize + 1)
                    AllProcessed.TrySetResult();
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public Task<int> ReleaseExpiredLocksAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<bool> CompleteCampaignIfFinishedAsync(Guid campaignId, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class RecordingProcessor : IDeliveryProcessingService
    {
        private int _captured;

        public TaskCompletionSource<string> Processed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Recovered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestedBatchSize { get; private set; }
        public DeliveryStatus ProcessedStatus { get; private set; }

        public Task<IReadOnlyList<Delivery>> CaptureBatchAsync(
            int batchSize, string workerId, TimeSpan lockDuration, CancellationToken ct)
        {
            RequestedBatchSize = batchSize;
            if (Interlocked.Exchange(ref _captured, 1) != 0)
                return Task.FromResult<IReadOnlyList<Delivery>>([]);

            var now = DateTime.UtcNow;
            var delivery = Delivery.Create(Guid.NewGuid(), Guid.NewGuid(), "user@example.com", now);
            delivery.StatusFromWaitingToInProgress(workerId, now + lockDuration, now);
            return Task.FromResult<IReadOnlyList<Delivery>>([delivery]);
        }

        public async Task ProcessAsync(Delivery delivery, CancellationToken ct)
        {
            ProcessedStatus = delivery.DeliveryStatus;
            await Recovered.Task.WaitAsync(ct);
            Processed.TrySetResult(delivery.WorkerId!);
        }

        public Task<int> ReleaseExpiredLocksAsync(CancellationToken ct)
        {
            Recovered.TrySetResult();
            return Task.FromResult(0);
        }

        public Task<bool> CompleteCampaignIfFinishedAsync(Guid campaignId, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
