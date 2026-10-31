using System.Diagnostics.Metrics;
using PackCall.Core.Diagnostics;

namespace PackCall.Core.Test;

[CollectionDefinition("Measurement", DisableParallelization = true)]
public sealed class MeasurementCollection;

[Collection("Measurement")]
public sealed class MeasurementCollectorTests
{
    private static MeasurementOptions Options => new() { Enabled = true, SessionId = Guid.NewGuid(), OutputDirectory = "unused" };
    private static readonly SeriesKey Counter = new((int)Event.TerminalApplied, Outcome.Sent, Purpose.None);
    private static readonly SeriesKey Duration = new((int)Operation.OperatorCall, Outcome.Accepted, Purpose.None);

    [Fact]
    public void EffectiveSettingsSerializationIsAnAllowlist()
    {
        // Unrelated configuration is deliberately not an input to the export DTO.
        var configuration = new Dictionary<string, string> { ["Password"] = "secret-canary", ["Parallelism"] = "4" };
        var exported = new EffectiveMeasurementSettings(int.Parse(configuration["Parallelism"]), 10, 90, 15, .25, 5, 5, false, "http");
        var json = System.Text.Json.JsonSerializer.Serialize(exported);
        Assert.DoesNotContain("secret-canary", json);
        Assert.DoesNotContain("Password", json);
        Assert.Throws<ArgumentException>(() => new MeasurementCollector(new() { Enabled = true }));
    }

    [Fact]
    public void DisabledHasNoListenerFilesOrWindowsAndIdentityIsPerStart()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        using var first = new MeasurementCollector(new() { OutputDirectory = directory });
        using var second = new MeasurementCollector(new());
        PerformanceMetrics.Count(Event.TerminalApplied, Outcome.Sent);
        first.Add(Counter, 10);
        Assert.Null(first.Close());
        Assert.NotEqual(first.InstanceId, second.InstanceId);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void MonotonicPartialAndEmptyWindowsPreserveTotalsDespiteWallClockJump()
    {
        var clock = new Clock();
        using var collector = new MeasurementCollector(Options, clock);
        collector.Add(Counter, 3);
        collector.Observe(Duration, 0);
        collector.Observe(Duration, 100);
        clock.Tick(2);
        clock.Utc += TimeSpan.FromHours(1);
        var first = collector.Close(true)!;
        Assert.Equal(2, first.DurationSeconds);
        Assert.True(first.ClockJump);
        Assert.True(first.Partial);
        Assert.Equal(new CounterWindow(3, 3), first.Counters[Counter]);
        Assert.Equal(2, first.Histograms[Duration].Count);
        Assert.Equal(100, first.Histograms[Duration].Sum);
        Assert.Equal(1, first.Histograms[Duration].Buckets[0]);
        clock.Tick(10);
        var empty = collector.Close()!;
        Assert.Equal(new CounterWindow(0, 3), empty.Counters[Counter]);
        Assert.Equal(0, empty.Histograms[Duration].Count);
        Assert.Null(empty.Histograms[Duration].Min);
        Assert.Equal(2, empty.ElapsedStartSeconds);
    }

    [Fact]
    public async Task ConcurrentClosingLosesNoEventsOrHistogramSamples()
    {
        using var collector = new MeasurementCollector(Options);
        var windows = new List<MeasurementWindow>();
        var writers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 10000; i++) { collector.Add(Counter, 1); collector.Observe(Duration, 2); }
        })).ToArray();
        while (writers.Any(t => !t.IsCompleted)) { windows.Add(collector.Close()!); await Task.Delay(1); }
        await Task.WhenAll(writers);
        windows.Add(collector.Close(true)!);
        Assert.Equal(80000, windows.Sum(w => w.Counters.GetValueOrDefault(Counter)?.Delta ?? 0));
        Assert.Equal(80000, windows.Sum(w => w.Histograms.GetValueOrDefault(Duration)?.Count ?? 0));
        Assert.Equal(160000, windows.Sum(w => w.Histograms.GetValueOrDefault(Duration)?.Sum ?? 0));
        Assert.Equal(80000, windows[^1].Counters[Counter].Cumulative);
        Assert.All(windows, w => Assert.All(w.Histograms.Values, h => Assert.Equal(h.Count, h.Buckets.Sum())));
    }

    [Fact]
    public void ListenerRecordsAndRejectsUnboundedCategories()
    {
        using var collector = new MeasurementCollector(Options);
        PerformanceMetrics.Count(Event.TerminalApplied, Outcome.Sent, 5);
        PerformanceMetrics.Duration(Operation.OperatorCall, Outcome.Accepted, 12);
        PerformanceMetrics.Count((Event)99999);
        var window = collector.Close()!;
        Assert.Equal(2, window.Counters.Count);
        Assert.Equal(5, window.Counters[Counter].Delta);
        Assert.Equal(12, window.Histograms[Duration].Sum);
        for (var i = 0; i < 10000; i++) collector.Observe(new(i, Outcome.Success, Purpose.None), 10);
        window = collector.Close()!;
        Assert.True(window.Histograms.Count + window.Counters.Count <= MeasurementCollector.MaximumSeries);
        Assert.True(window.DroppedMeasurements > 0);
        // One active + one closed copy of all bounded bucket arrays is well below the 16 MiB budget.
        Assert.True(2L * MeasurementCollector.MaximumSeries * (MeasurementCollector.Bounds.Count + 1) * sizeof(long) < 4 * 1024 * 1024);
    }

    [Fact]
    public void ThrowingCallbackCannotChangeBusinessResult()
    {
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PerformanceMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new IOException("secret-canary"));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new IOException("secret-canary"));
        listener.Start();
        PerformanceMetrics.Count(Event.TerminalApplied);
        PerformanceMetrics.Duration(Operation.ResultPersist, Outcome.Applied, 1);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public DateTimeOffset Utc = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
        public void Tick(int seconds) { _ticks += seconds * 1000; Utc += TimeSpan.FromSeconds(seconds); }
    }
}
