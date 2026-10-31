using System.Diagnostics;
using System.Text.Json;

namespace PackCall.Core.Diagnostics;

public sealed class RuntimeSampler(Guid sessionId, Guid processInstanceId, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Guid _instanceId = Guid.NewGuid();
    private readonly long _boot = (time ?? TimeProvider.System).GetTimestamp();
    private long _sequence, _previousAllocated = GC.GetTotalAllocatedBytes();
    public Guid InstanceId => _instanceId;

    public byte[] Collect(double periodSeconds = 5, double lagSeconds = 0)
    {
        var start = _time.GetTimestamp();
        var utc = _time.GetUtcNow();
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime.TotalSeconds;
        var allocated = GC.GetTotalAllocatedBytes();
        var memory = GC.GetGCMemoryInfo();
        Dictionary<string, object> metrics = new();
        void Add(string name, string unit, string kind, double? value, string? missing = null) =>
            metrics[name] = new { unit, kind, value, missing_reason = missing };
        Add("process.cpu", "s", "cumulative", cpuStart);
        Add("process.rss", "bytes", "gauge", process.WorkingSet64);
        Add("process.private", "bytes", "gauge", process.PrivateMemorySize64);
        Add("runtime.heap", "bytes", "gauge", memory.HeapSizeBytes);
        Add("runtime.allocated", "bytes", "cumulative", allocated);
        Add("runtime.allocated_delta", "bytes", "delta", Math.Max(0, allocated - _previousAllocated));
        _previousAllocated = allocated;
        for (var generation = 0; generation <= GC.MaxGeneration; generation++)
            Add($"runtime.gc.gen{generation}", "events", "cumulative", GC.CollectionCount(generation));
        Add("runtime.gc.pause", "s", "cumulative", GC.GetTotalPauseDuration().TotalSeconds);
        Add("runtime.threadpool.queue", "events", "gauge", ThreadPool.PendingWorkItemCount);
        Add("runtime.threadpool.threads", "threads", "gauge", ThreadPool.ThreadCount);
        Add("runtime.exceptions", "events", "cumulative", null, "unsupported");
        process.Refresh();
        Add("collector.cpu", "s", "delta", Math.Max(0, process.TotalProcessorTime.TotalSeconds - cpuStart));
        Add("collector.rss", "bytes", "gauge", process.WorkingSet64);
        var end = _time.GetTimestamp();
        Add("collector.duration", "s", "gauge", _time.GetElapsedTime(start, end).TotalSeconds);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 1, session_id = sessionId, source = "runtime", instance_id = _instanceId,
            process_instance_id = processInstanceId, sequence = _sequence++, start_utc = utc, end_utc = _time.GetUtcNow(),
            duration_s = _time.GetElapsedTime(start, end).TotalSeconds,
            elapsed_start_s = _time.GetElapsedTime(_boot, start).TotalSeconds,
            elapsed_end_s = _time.GetElapsedTime(_boot, end).TotalSeconds, quality = "complete",
            period_s = periodSeconds, lag_s = lagSeconds, receipt_utc = _time.GetUtcNow(), metrics
        });
    }
}
