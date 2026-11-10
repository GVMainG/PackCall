using System.Diagnostics.Metrics;

namespace PackCall.Core.Diagnostics;

public readonly record struct SeriesKey(int Metric, Outcome Outcome, Purpose Purpose);
public sealed record CounterWindow(long Delta, long Cumulative);
public sealed record HistogramWindow(long[] Buckets, long Count, double Sum, double? Min, double? Max);
public sealed record MeasurementWindow(Guid SessionId, Guid InstanceId, long Sequence,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, double DurationSeconds,
    double ElapsedStartSeconds, double ElapsedEndSeconds, bool Partial, bool ClockJump,
    long DroppedMeasurements, IReadOnlyDictionary<SeriesKey, CounterWindow> Counters,
    IReadOnlyDictionary<SeriesKey, HistogramWindow> Histograms);

/// <summary>Single lock makes record/window-close linearizable. No I/O or timers on this path.</summary>
public sealed class MeasurementCollector : IDisposable
{
    public const int MaximumSeries = 512;
    public static readonly IReadOnlyList<double> Bounds = Array.AsReadOnly(CreateBounds());
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly MeasurementOptions _options;
    private readonly Dictionary<SeriesKey, (long Delta, long Total)> _counters = new();
    private readonly Dictionary<SeriesKey, Histogram> _histograms = new();
    private readonly MeterListener? _listener;
    private readonly long _boot;
    private long _start, _sequence, _dropped;
    private DateTimeOffset _startUtc;
    public Guid InstanceId { get; } = Guid.NewGuid();
    public bool Enabled => _options.Enabled;

    public MeasurementCollector(MeasurementOptions options, TimeProvider? time = null)
    {
        options.Validate();
        _options = options;
        _time = time ?? TimeProvider.System;
        _boot = _start = _time.GetTimestamp();
        _startUtc = _time.GetUtcNow();
        if (!Enabled) return;
        _counters[new((int)Event.TerminalApplied, Outcome.Sent, Purpose.None)] = (0, 0);
        _counters[new((int)Event.TerminalApplied, Outcome.Failed, Purpose.None)] = (0, 0);
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PerformanceMetrics.MeterName && instrument.Name is "events" or "duration")
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            try { if (Key(tags, false) is { } key) Add(key, value); } catch { Interlocked.Increment(ref _dropped); }
        });
        _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            try { if (Key(tags, true) is { } key) Observe(key, value); } catch { Interlocked.Increment(ref _dropped); }
        });
        _listener.Start();
    }

    private static SeriesKey? Key(ReadOnlySpan<KeyValuePair<string, object?>> tags, bool histogram)
    {
        if (tags.Length != 3 || tags[0].Key != "metric" || tags[1].Key != "outcome" || tags[2].Key != "purpose" ||
            tags[0].Value is not int metric || tags[1].Value is not int outcome || tags[2].Value is not int purpose ||
            !Enum.IsDefined((Outcome)outcome) || !Enum.IsDefined((Purpose)purpose) ||
            (histogram ? !Enum.IsDefined((Operation)metric) : !Enum.IsDefined((Event)metric))) return null;
        return new(metric, (Outcome)outcome, (Purpose)purpose);
    }

    public void Add(SeriesKey key, long value)
    {
        if (!Enabled || value < 0) return;
        lock (_gate)
        {
            if (!_counters.ContainsKey(key) && _counters.Count + _histograms.Count >= MaximumSeries) { _dropped++; return; }
            var previous = _counters.GetValueOrDefault(key);
            _counters[key] = (checked(previous.Delta + value), checked(previous.Total + value));
        }
    }

    public void Observe(SeriesKey key, double milliseconds)
    {
        if (!Enabled || !double.IsFinite(milliseconds) || milliseconds < 0) return;
        lock (_gate)
        {
            if (!_histograms.TryGetValue(key, out var histogram))
            {
                if (_counters.Count + _histograms.Count >= MaximumSeries) { _dropped++; return; }
                _histograms[key] = histogram = new();
            }
            histogram.Record(milliseconds);
        }
    }

    public MeasurementWindow? Close(bool partial = false)
    {
        if (!Enabled) return null;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var utc = _time.GetUtcNow();
            var duration = _time.GetElapsedTime(_start, now).TotalSeconds;
            var counters = _counters.ToDictionary(x => x.Key, x => new CounterWindow(x.Value.Delta, x.Value.Total));
            var histograms = _histograms.ToDictionary(x => x.Key, x => x.Value.Snapshot());
            var window = new MeasurementWindow(_options.SessionId, InstanceId, _sequence++, _startUtc, utc, duration,
                _time.GetElapsedTime(_boot, _start).TotalSeconds, _time.GetElapsedTime(_boot, now).TotalSeconds,
                partial, Math.Abs((utc - _startUtc).TotalSeconds - duration) > 1,
                Interlocked.Exchange(ref _dropped, 0), counters, histograms);
            foreach (var key in _counters.Keys) _counters[key] = (0, _counters[key].Total);
            foreach (var histogram in _histograms.Values) histogram.Clear();
            _start = now;
            _startUtc = utc;
            return window;
        }
    }

    public void Dispose() => _listener?.Dispose();

    private static double[] CreateBounds()
    {
        List<double> result = [0];
        for (var k = 0; result[^1] < 600000; k++) result.Add(0.01 * Math.Pow(1.1, k));
        return result.ToArray();
    }

    private sealed class Histogram
    {
        private readonly long[] _buckets = new long[Bounds.Count + 1];
        private long _count;
        private double _sum;
        private double? _min, _max;
        public void Record(double value)
        {
            var low = 0;
            var high = Bounds.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (value <= Bounds[middle]) high = middle; else low = middle + 1;
            }
            _buckets[low]++;
            _count++;
            _sum += value;
            _min = _min is null ? value : Math.Min(_min.Value, value);
            _max = _max is null ? value : Math.Max(_max.Value, value);
        }
        public HistogramWindow Snapshot() => new((long[])_buckets.Clone(), _count, _sum, _min, _max);
        public void Clear() { Array.Clear(_buckets); _count = 0; _sum = 0; _min = _max = null; }
    }
}
