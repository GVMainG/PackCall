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
