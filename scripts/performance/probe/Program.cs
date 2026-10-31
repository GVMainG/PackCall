using PackCall.Core.Diagnostics;
using PackCall.Infrastructure.Hosting;

// Deliberately never exported; used by the external crash/privacy test.
var canary = Environment.GetEnvironmentVariable("PERFORMANCE_SECRET_CANARY");
using var host = new MeasurementHost(new()
{
    Enabled = true, SessionId = Guid.Parse(args[1]), OutputDirectory = args[0],
    Interval = TimeSpan.FromMilliseconds(200)
}, "worker", "synthetic-probe");
await host.StartAsync(default);
PerformanceMetrics.Count(Event.TerminalApplied, Outcome.Sent, 3);
PerformanceMetrics.Duration(Operation.CaptureStorage, Outcome.Success, 1.25);
await MeasuredOperation.Run(Operation.OperatorCall, () => Task.Delay(Timeout.InfiniteTimeSpan));
GC.KeepAlive(canary);
