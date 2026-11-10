using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PackCall.Core.Diagnostics;
using System.Text.Json;

namespace PackCall.Infrastructure.Hosting;

public static class MeasurementHosting
{
    public static IServiceCollection AddPerformanceMeasurement(this IServiceCollection services, IConfiguration configuration,
        string role, string? workerId = null)
    {
        var section = configuration.GetSection("PerformanceMeasurement");
        var options = new MeasurementOptions
        {
            Enabled = section.GetValue<bool>("Enabled"),
            SessionId = section.GetValue<Guid>("SessionId"),
            OutputDirectory = section["OutputDirectory"] ?? "",
            Interval = section.GetValue("Interval", TimeSpan.FromSeconds(10))
        };
        options.Validate();
        if (options.Enabled)
        {
            var sender = TestOperatorSettings.Parse(configuration);
            var worker = configuration.GetSection("DeliveryWorker");
            var effective = new EffectiveMeasurementSettings(worker.GetValue("Parallelism", 4), worker.GetValue("BatchSize", 10),
                worker.GetValue("LockDuration", TimeSpan.FromSeconds(90)).TotalSeconds,
                worker.GetValue("RecoveryInterval", TimeSpan.FromSeconds(15)).TotalSeconds,
                worker.GetValue("IdleDelay", TimeSpan.FromMilliseconds(250)).TotalSeconds,
                worker.GetValue("ErrorDelay", TimeSpan.FromSeconds(5)).TotalSeconds,
                sender.Timeout?.TotalSeconds ?? 5, sender.SimulateErrors, sender.Mode.ToString().ToLowerInvariant());
            services.AddSingleton<IHostedService>(_ => new MeasurementHost(options, role, workerId, effective));
        }
        return services;
    }
}

public sealed class MeasurementHost : IHostedService, IDisposable
{
    private readonly MeasurementOptions _options;
    private readonly string _role;
    private readonly string? _workerId;
    private readonly MeasurementCollector _collector;
    private readonly MeasurementFileWriter _writer;
    private readonly MeasurementFileWriter _runtimeWriter;
    private readonly RuntimeSampler _runtime;
    private readonly CancellationTokenSource _sampling = new();
    private readonly CancellationTokenSource _writing = new();
    private Task _sampler = Task.CompletedTask, _consumer = Task.CompletedTask, _runtimeTask = Task.CompletedTask, _runtimeConsumer = Task.CompletedTask;
    private bool _started;
    private bool _samplingFailed;
    private readonly EffectiveMeasurementSettings? _effective;

    public MeasurementHost(MeasurementOptions options, string role, string? workerId, EffectiveMeasurementSettings? effective = null)
    {
        if (role is not ("worker" or "api")) throw new ArgumentException("Invalid measurement role.");
        _options = options;
        _role = role;
        _workerId = workerId;
        _effective = effective;
        _collector = new(options);
        _writer = new(options.OutputDirectory, _collector.InstanceId);
        _runtimeWriter = new(options.OutputDirectory, _collector.InstanceId, prefix: "runtime", maximumRecordBytes: 65536);
        _runtime = new(options.SessionId, _collector.InstanceId);
    }

    public Task StartAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_writer.DirectoryPath);
            MeasurementFileWriter.AtomicWrite(Path.Combine(_writer.DirectoryPath, "process.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = 1, session_id = _options.SessionId, instance_id = _collector.InstanceId,
                source = _role, worker_id = _workerId, boot_utc = DateTimeOffset.UtcNow,
                interval_s = _options.Interval.TotalSeconds, runtime_instance_id = _runtime.InstanceId, state = "ready",
                effective_settings = _effective
            }));
            _consumer = Task.Run(() => _writer.RunAsync(_writing.Token), CancellationToken.None);
            _runtimeConsumer = Task.Run(() => _runtimeWriter.RunAsync(_writing.Token), CancellationToken.None);
            _sampler = SampleAsync();
            _runtimeTask = SampleRuntimeAsync();
            _started = true;
        }
        catch { /* Driver detects absence of the ready file; application behaviour is preserved. */ }
        return Task.CompletedTask;
    }

    private async Task SampleAsync()
    {
        using var timer = new PeriodicTimer(_options.Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_sampling.Token)) Enqueue(false);
        }
        catch (OperationCanceledException) when (_sampling.IsCancellationRequested) { }
        catch { _samplingFailed = true; }
    }

    private async Task SampleRuntimeAsync()
    {
        try
        {
            while (!_sampling.IsCancellationRequested)
            {
                _runtimeWriter.TryEnqueue(_runtime.Collect());
                // Delay from completion: never catch up after a slow sample.
                await Task.Delay(TimeSpan.FromSeconds(5), _sampling.Token);
            }
        }
        catch (OperationCanceledException) when (_sampling.IsCancellationRequested) { }
        catch { _samplingFailed = true; }
    }

    private void Enqueue(bool partial)
    {
        if (_collector.Close(partial) is { } window)
            _writer.TryEnqueue(MeasurementJson.Interval(window, _role, _writer.Dropped));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (!_started) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        _sampling.Cancel();
        try
        {
            await Task.WhenAll(_sampler, _runtimeTask).WaitAsync(budget.Token);
            Enqueue(true);
            _writer.Complete();
            _runtimeWriter.Complete();
            await Task.WhenAll(_consumer, _runtimeConsumer).WaitAsync(budget.Token);
            MeasurementFileWriter.AtomicWrite(Path.Combine(_writer.DirectoryPath, "stopped.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = 1, session_id = _options.SessionId, instance_id = _collector.InstanceId,
                end_utc = DateTimeOffset.UtcNow, state = "process_stopped",
                telemetry_status = _samplingFailed || _writer.Failed || _writer.Dropped > 0 || _runtimeWriter.Failed || _runtimeWriter.Dropped > 0 ? "partial" : "complete",
                dropped_intervals = _writer.Dropped
            }));
        }
        catch { _writing.Cancel(); /* No completed marker if the shutdown budget expires. */ }
    }

    public void Dispose()
    {
        _sampling.Cancel();
        _writing.Cancel();
        _collector.Dispose();
    }
}
