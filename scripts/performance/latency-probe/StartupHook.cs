using System.Diagnostics.Metrics;
using System.Text.Json;

internal static class StartupHook
{
    private const int Capacity = 300_000;
    // PackCall.Core.Diagnostics.Operation.ProcessingTotal in the pinned beta.2 tag.
    private const int ProcessingTotal = 4;

    public static void Initialize()
    {
        var path = Environment.GetEnvironmentVariable("PACKCALL_LATENCY_OUTPUT")
            ?? throw new InvalidOperationException("Missing latency output path.");
        var durations = new double[Capacity];
        var gate = new object();
        var count = 0;
        var overflow = 0L;
        var invalid = 0L;
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "PackCall.Performance" && instrument.Meter.Version == "1"
                && instrument.Name == "duration" && instrument.Unit == "ms" && instrument is Histogram<double>)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, duration, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key != "metric" || tag.Value is not int metric || metric != ProcessingTotal)
                    continue;
                lock (gate)
                {
                    if (!double.IsFinite(duration) || duration < 0)
                        invalid++;
                    else if (count == Capacity)
                        overflow++;
                    else
                        durations[count++] = duration;
                }
                break;
            }
        });
        listener.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            listener.Dispose();
            lock (gate)
            {
                var temporary = path + ".tmp";
                using (var stream = File.Create(temporary))
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schema_version", 1);
                    writer.WriteNumber("capacity", Capacity);
                    writer.WriteNumber("count", count);
                    writer.WriteNumber("overflow", overflow);
                    writer.WriteNumber("invalid", invalid);
                    writer.WriteStartArray("durations_ms");
                    for (var index = 0; index < count; index++)
                        writer.WriteNumberValue(durations[index]);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                File.Move(temporary, path);
            }
        };
    }
}
