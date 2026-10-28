using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PackCall.Core;
using PackCall.Core.Sending;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;
using PackCall.Worker.Background;

// Acceptance-only process: deterministic barriers without production test switches.
var builder = Host.CreateApplicationBuilder(args);
PackCallHosting.LoadConfiguration(builder.Configuration, builder.Environment.EnvironmentName, args);
builder.Services.AddPackCallLogging(builder.Configuration, "Worker.Probe");
builder.Services.AddStorage(PackCallHosting.ConnectionString(builder.Configuration));
builder.Services.AddDeliveryProcessing(PackCall.Infrastructure.Hosting.TestOperatorSettings.Parse(builder.Configuration));
builder.Services.AddSchemaGate();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.Configure<DeliveryWorkerOptions>(o => { o.Parallelism = 1; o.BatchSize = 1; });
var mode = Environment.GetEnvironmentVariable("PROBE_MODE");
if (mode == "before") builder.Services.AddSingleton<ILeaseClock, BarrierClock>();
else builder.Services.AddSingleton<IMessageSender, UncooperativeSender>();
builder.Services.AddHostedService<DeliveryWorker>();
await builder.Build().RunAsync();

sealed class BarrierClock : ILeaseClock
{
    public async Task<DateTime> GetUtcNowAsync(CancellationToken ct)
    {
        File.WriteAllText("/tmp/packcall-probe-entered", "before sender");
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        throw new InvalidOperationException("unreachable");
    }
}
sealed class UncooperativeSender : IMessageSender
{
    public async Task<SendResult> SendAsync(SendRequest request, bool simulateErrors, CancellationToken ct)
    {
        File.WriteAllText("/tmp/packcall-probe-entered", "sender ignoring cancellation");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return SendResult.Success();
    }
}
