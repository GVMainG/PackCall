using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PackCall.Core.Diagnostics;
using PackCall.Core.Entities;
using PackCall.Core.Sending;
using PackCall.Core.Services;

// Only the acceptance host registers these controls. All database work is delegated to the production service.
sealed class Sm6Controls
{
    private int _attempts;

    private Sm6Controls(string scenario)
    {
        Scenario = scenario;
        DirectoryPath = Path.Combine("/control", scenario);
        Directory.CreateDirectory(DirectoryPath);
    }

    public string Scenario { get; }
    private string DirectoryPath { get; }

    public static void Register(IServiceCollection services, string scenario, Func<IMessageSender> createSender)
    {
        if (scenario is not ("timeout" or "exception" or "cancellation" or "mismatch" or "rejection"
            or "worker-loss" or "recovery" or "stale-result"))
            throw new InvalidOperationException("Unknown SM-6 scenario");

        services.AddSingleton(new Sm6Controls(scenario));
        services.AddSingleton<IMessageSender>(sp => new Sm6Sender(sp.GetRequiredService<Sm6Controls>(), createSender()));
        services.RemoveAll<IDeliveryProcessingService>();
        services.AddScoped<DeliveryProcessingService>();
        services.AddScoped<IDeliveryProcessingService, CaptureBarrier>();
    }

    // Recovery keeps running while capture waits, exposing the released row in Waiting.
    public bool CanCapture => (Scenario != "recovery" && Volatile.Read(ref _attempts) == 0) || Exists("allow-recapture");
    public bool HoldResult => Scenario is "worker-loss" or "recovery";
    public int LastAttempt => Scenario is "rejection" or "worker-loss" or "recovery" or "stale-result" ? 1 : 2;

    public int StartAttempt() => Interlocked.Increment(ref _attempts);

    public async Task WaitForAsync(string file, CancellationToken ct)
    {
        while (!Exists(file))
            await Task.Delay(25, ct);
        ct.ThrowIfCancellationRequested();
    }

    public void Record(int attempt, Guid deliveryId, string phase, string? outcome = null, bool leaseCancelled = false)
        => Write($"attempt-{attempt}-{phase}.json", new { attempt, deliveryId, phase, outcome, leaseCancelled });

    public void RecordCapture(IReadOnlyList<Delivery> captured)
    {
        if (Scenario == "worker-loss" && captured.Count > 0)
            Write("captured-delivery.json", captured.Single());
    }

    public void RecordStaleResult(Guid deliveryId, Outcome outcome)
        => Write("processing.json", new { deliveryId, outcome = outcome.ToString() });

    private void Write(string file, object data)
    {
        var path = Path.Combine(DirectoryPath, file);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(data, JsonOptions.Value));
        File.Move(path + ".tmp", path);
    }

    private bool Exists(string file) => File.Exists(Path.Combine(DirectoryPath, file));
}

sealed class CaptureBarrier(DeliveryProcessingService inner, Sm6Controls controls) : IDeliveryProcessingService
{
    public async Task<IReadOnlyList<Delivery>> CaptureBatchAsync(int batchSize, string workerId, TimeSpan lockDuration, CancellationToken ct)
    {
        if (!controls.CanCapture)
            return [];

        var captured = await inner.CaptureBatchAsync(batchSize, workerId, lockDuration, ct);
        controls.RecordCapture(captured);
        return captured;
    }

    public Task ProcessAsync(Delivery delivery, CancellationToken ct) => inner.ProcessAsync(delivery, ct);
    public Task<int> ReleaseExpiredLocksAsync(CancellationToken ct) => inner.ReleaseExpiredLocksAsync(ct);
    public Task<bool> CompleteCampaignIfFinishedAsync(Guid campaignId, CancellationToken ct) =>
        inner.CompleteCampaignIfFinishedAsync(campaignId, ct);
}

sealed class Sm6Sender(Sm6Controls controls, IMessageSender inner) : IMessageSender, IDisposable
{
    public async Task<SendResult> SendAsync(SendRequest request, bool simulateErrors, CancellationToken ct)
    {
        var attempt = controls.StartAttempt();
        controls.Record(attempt, request.DeliveryId, "entered");
        if (attempt > controls.LastAttempt)
            throw new InvalidOperationException("Unexpected extra SM-6 operator attempt");

        try
        {
            await controls.WaitForAsync($"allow-attempt-{attempt}", ct);
        }
        catch (OperationCanceledException)
        {
            controls.Record(attempt, request.DeliveryId, "finished", "barrier-cancelled");
            throw;
        }

        SendResult result;
        try
        {
            result = await SendScriptedAsync(attempt, request, simulateErrors, ct);
        }
        catch (Exception error)
        {
            var leaseCancelled = ct.IsCancellationRequested
                && error is OperationCanceledException cancellation
                && cancellation.CancellationToken == ct;
            controls.Record(attempt, request.DeliveryId, "finished", error.GetType().Name, leaseCancelled);
            throw;
        }
        controls.Record(attempt, request.DeliveryId, "finished", result.IsSuccess ? "accepted" : result.Error);
        if (controls.HoldResult)
            await controls.WaitForAsync("allow-result", ct);
        return result;
    }

    private async Task<SendResult> SendScriptedAsync(int attempt, SendRequest request, bool simulateErrors, CancellationToken ct)
    {
        if (attempt == 1)
        {
            switch (controls.Scenario)
            {
                case "exception":
                    throw new InvalidOperationException("SM-6 scripted operator exception");
                case "cancellation":
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    throw new InvalidOperationException("Expected lease cancellation");
                case "rejection":
                    return SendResult.Failure("SimulatedOperatorRejected");
            }
        }
        return await inner.SendAsync(request, simulateErrors, ct);
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
