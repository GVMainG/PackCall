using System.Diagnostics.Metrics;

namespace PackCall.Core.Diagnostics;

public enum Operation
{
    CaptureStorage, CaptureTotal, BatchWait, LeaseCheck, ProcessingTotal,
    OperatorCall, ResultPersist, JournalAppend, CampaignCheck, CampaignPersist,
    RecoveryStorage, RecoveryTotal
}

public enum Outcome
{
    Success, Error, Cancelled, Accepted, Rejected, Validation, Service, Protocol,
    Transport, Timeout, Other, Applied, NotApplied, TerminalPathComplete,
    StaleResult, PausedRelease, PausedNoRelease, LeaseSkipped, InvalidCapture,
    Sent, Failed, Host, Lease
}

public enum Purpose { None, Capture, Terminal, Recovery, Pause }
public enum Event
{
    TerminalApplied, ProcessingFinished, OperatorFinished, ResultFinished,
    CaptureCalls, CaptureEmpty, CaptureRows, ProcessingStarted, ServiceStarted,
    JournalFinished, CampaignChecks, CampaignCompleted, RecoveryCalls, RecoveryRows,
    AfterTerminalApplied, Cancellation
}

/// <summary>Значения меток ограничены перечислениями. Ошибки обработчиков метрик не прерывают работу приложения.</summary>
public static class PerformanceMetrics
{
    public const string MeterName = "PackCall.Performance";
    private static readonly Meter Meter = new(MeterName, "1");
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("events");
    private static readonly Histogram<double> Durations = Meter.CreateHistogram<double>("duration", "ms");
    public static bool Enabled => Events.Enabled || Durations.Enabled;

    public static void Count(Event metric, Outcome outcome = Outcome.Success, long value = 1, Purpose purpose = Purpose.None)
    {
        if (!Events.Enabled) return;
        try { Events.Add(value, new("metric", (int)metric), new("outcome", (int)outcome), new("purpose", (int)purpose)); }
        catch { /* Диагностика не должна влиять на результат доставки. */ }
    }

    public static void Duration(Operation operation, Outcome outcome, double milliseconds, Purpose purpose = Purpose.None)
    {
        if (!Durations.Enabled) return;
        try { Durations.Record(milliseconds, new("metric", (int)operation), new("outcome", (int)outcome), new("purpose", (int)purpose)); }
        catch { /* Сюда входят ошибки сторонних подписчиков на метрики. */ }
    }
}
