using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using PackCall.Core;
using PackCall.Core.Entities;
using PackCall.Core.Services;
using PackCall.Core.Sending;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;
using PackCall.Infrastructure.Sending;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PackCall.Worker.Background;

var builder = Host.CreateApplicationBuilder(args);
PackCallHosting.LoadConfiguration(builder.Configuration, builder.Environment.EnvironmentName, args);
var id = Environment.GetEnvironmentVariable("IDENTITY") ?? $"acceptance-{Guid.NewGuid():N}";
builder.Services.AddPackCallLogging(builder.Configuration, "Workers.Acceptance", id);
builder.Services.AddStorage(PackCallHosting.ConnectionString(builder.Configuration) + ";Application Name=" + id);
builder.Services.AddDeliveryProcessing(PackCall.Infrastructure.Hosting.TestOperatorSettings.Parse(builder.Configuration));
builder.Services.AddSingleton(new WorkerIdentity(id));
var settings = TestOperatorSettings.Parse(builder.Configuration);
builder.Services.RemoveAll<IMessageSender>();
builder.Services.AddSingleton<IMessageSender>(sp => new BarrierSender(sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<WorkerIdentity>(), settings.Mode == TestSenderMode.Http
        ? new HttpTestEmailSender(settings) : new MockEmailSender(new OperatorRandom())));
builder.Services.AddSchemaGate();
builder.Services.Configure<DeliveryWorkerOptions>(o => {
    o.Parallelism = 1; o.BatchSize = 1; o.LockDuration = TimeSpan.FromSeconds(20);
    o.RecoveryInterval = TimeSpan.FromMilliseconds(100); o.IdleDelay = TimeSpan.FromMilliseconds(100);
});
var stale = Environment.GetEnvironmentVariable("STALE_FILE");
if (stale is null) builder.Services.AddHostedService<DeliveryWorker>();
using var host = builder.Build();
await host.StartAsync();
if (stale is not null)
{
    var delivery = JsonSerializer.Deserialize<Delivery>(File.ReadAllText(stale), JsonOptions.Value)!;
    using var scope = host.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>().ProcessAsync(delivery, CancellationToken.None);
    Console.WriteLine("Stale ProcessAsync reached production repository with uncancelled token");
    await host.StopAsync();
}
else await host.WaitForShutdownAsync();

sealed class BarrierSender(IServiceScopeFactory scopes, WorkerIdentity identity, IMessageSender inner) : IMessageSender, IDisposable
{
    public async Task<SendResult> SendAsync(SendRequest request, bool simulateErrors, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Deliveries.AsNoTracking().SingleAsync(d => d.Id == request.DeliveryId, ct);
        var dbNow = await scope.ServiceProvider.GetRequiredService<ILeaseClock>().GetUtcNowAsync(ct);
        var directory = "/control/" + identity.Id;
        Directory.CreateDirectory(directory);
        // The parent observes only atomically renamed complete files.
        var attemptId = Guid.NewGuid().ToString("N");
        var snapshot = directory + "/" + request.DeliveryId + "-" + attemptId + ".json";
        await File.WriteAllTextAsync(snapshot + ".tmp", JsonSerializer.Serialize(new { row, appNow = DateTime.UtcNow, dbNow, pid = Environment.ProcessId }, JsonOptions.Value), ct);
        File.Move(snapshot + ".tmp", snapshot);
        while (!File.Exists(directory + "/release")) await Task.Delay(25, ct);
        return await inner.SendAsync(request, simulateErrors, ct);
    }
    public void Dispose() { (inner as IDisposable)?.Dispose(); }
}
static class JsonOptions
{
    public static readonly JsonSerializerOptions Value = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver {
            Modifiers = { info => {
                if (info.Type != typeof(Delivery)) return;
                info.CreateObject = () => Activator.CreateInstance(typeof(Delivery), true)!;
                foreach (var property in info.Properties)
                {
                    var member = typeof(Delivery).GetProperty(property.Name)!;
                    if (member.SetMethod is not null) property.Set = (obj, value) => member.SetValue(obj, value);
                }
            }}
        };
        return options;
    }
}
