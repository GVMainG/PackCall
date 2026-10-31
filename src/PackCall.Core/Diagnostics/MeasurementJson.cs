using System.Text.Json;

namespace PackCall.Core.Diagnostics;

public static class MeasurementJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static byte[] Interval(MeasurementWindow window, string role, long writerDropped = 0)
    {
        Dictionary<string, object> metrics = new();
        foreach (var (key, value) in window.Counters)
        {
            var name = CounterName((Event)key.Metric);
            var suffix = Labels(key);
            var unit = (Event)key.Metric is Event.TerminalApplied or Event.CaptureRows or Event.RecoveryRows ? "deliveries" : "events";
            metrics[$"{name}{suffix}.delta"] = Scalar("delta", unit, value.Delta);
            metrics[$"{name}{suffix}.cumulative"] = Scalar("cumulative", unit, value.Cumulative);
        }
        foreach (var (key, value) in window.Histograms)
        {
            metrics[$"{OperationName((Operation)key.Metric)}{Labels(key)}"] = new
            {
                kind = "histogram", unit = "ms", bounds_ms = MeasurementCollector.Bounds,
                buckets = value.Buckets, count = value.Count, sum = value.Sum, min = value.Min, max = value.Max
            };
        }
        metrics["diagnostics.dropped_measurements.delta"] = Scalar("delta", "events", window.DroppedMeasurements);
        metrics["diagnostics.dropped_intervals.cumulative"] = Scalar("cumulative", "events", writerDropped);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 1, session_id = window.SessionId, source = role, instance_id = window.InstanceId,
            sequence = window.Sequence, start_utc = window.StartUtc, end_utc = window.EndUtc,
            duration_s = window.DurationSeconds, elapsed_start_s = window.ElapsedStartSeconds,
            elapsed_end_s = window.ElapsedEndSeconds,
            quality = window.ClockJump ? "clock_jump" : window.Partial || window.DroppedMeasurements > 0 || writerDropped > 0 ? "partial" : "complete",
            metrics
        }, Options);
    }

    private static object Scalar(string kind, string unit, long value) => new { kind, unit, value, missing_reason = (string?)null };
    private static string Labels(SeriesKey key) => "." + JsonNamingPolicy.SnakeCaseLower.ConvertName(key.Outcome.ToString()) +
        (key.Purpose == Purpose.None ? "" : "." + key.Purpose.ToString().ToLowerInvariant());

    public static string CounterName(Event value) => value switch
    {
        Event.TerminalApplied => "terminal", Event.ProcessingFinished => "processing.finished",
        Event.OperatorFinished => "operator.finished", Event.ResultFinished => "result.finished",
        Event.CaptureCalls => "capture.calls", Event.CaptureEmpty => "capture.empty", Event.CaptureRows => "capture.rows",
        Event.ProcessingStarted => "processing.started", Event.ServiceStarted => "service.started",
        Event.JournalFinished => "journal.finished", Event.CampaignChecks => "campaign.checks",
        Event.CampaignCompleted => "campaign.completed", Event.RecoveryCalls => "recovery.calls",
        Event.RecoveryRows => "recovery.rows", Event.AfterTerminalApplied => "processing.after_terminal_applied",
        Event.Cancellation => "processing.cancellation", _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static string OperationName(Operation value) => value switch
    {
        Operation.CaptureStorage => "capture.storage", Operation.CaptureTotal => "capture.total",
        Operation.BatchWait => "batch.wait", Operation.LeaseCheck => "lease.check",
        Operation.ProcessingTotal => "processing.total", Operation.OperatorCall => "operator.call",
        Operation.ResultPersist => "result.persist", Operation.JournalAppend => "journal.append",
        Operation.CampaignCheck => "campaign.check", Operation.CampaignPersist => "campaign.persist",
        Operation.RecoveryStorage => "recovery.storage", Operation.RecoveryTotal => "recovery.total",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
