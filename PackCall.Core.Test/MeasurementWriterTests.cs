using System.Text;
using System.Text.Json;
using PackCall.Core.Diagnostics;
using PackCall.Infrastructure.Hosting;

namespace PackCall.Core.Test;

[Collection("Measurement")]
public sealed class MeasurementWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "packcall-measurement-test-" + Guid.NewGuid());

    [Fact]
    public void BoundedRetainedTelemetryStateFitsSixteenMiB()
    {
        var before = GC.GetTotalMemory(true);
        using var collector = new MeasurementCollector(new() { Enabled = true, SessionId = Guid.NewGuid(), OutputDirectory = _root });
        for (var i = 0; i < MeasurementCollector.MaximumSeries; i++)
            collector.Observe(new(i % 12, (Outcome)((i / 12) % 23), (Purpose)(i / (12 * 23))), 10);
        var closed = collector.Close();
        var writer = new MeasurementFileWriter(_root, collector.InstanceId);
        var runtime = new MeasurementFileWriter(_root, collector.InstanceId, prefix: "runtime", maximumRecordBytes: 65536);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(writer.TryEnqueue(new byte[MeasurementFileWriter.MaximumRecordBytes]));
            Assert.True(runtime.TryEnqueue(new byte[65536]));
        }
        var currentSerialization = new byte[MeasurementFileWriter.MaximumRecordBytes];
        var retained = GC.GetTotalMemory(true) - before;
        Assert.InRange(retained, 0, 16L * 1024 * 1024);
        GC.KeepAlive(closed); GC.KeepAlive(writer); GC.KeepAlive(runtime); GC.KeepAlive(currentSerialization);
    }

    [Fact]
    public async Task QueueIsBoundedAndRotationPreservesEarlyFiles()
    {
        var writer = new MeasurementFileWriter(_root, Guid.NewGuid(), rotationBytes: 10);
        var bytes = Encoding.UTF8.GetBytes("{\"n\":1}");
        Assert.True(writer.TryEnqueue(bytes));
        Assert.True(writer.TryEnqueue(bytes));
        Assert.True(writer.TryEnqueue(bytes));
        Assert.False(writer.TryEnqueue(bytes));
        Assert.Equal(1, writer.Dropped);
        writer.Complete();
        await writer.RunAsync();
        Assert.False(writer.Failed);
        var files = Directory.GetFiles(writer.DirectoryPath, "intervals-*.jsonl");
        Assert.Equal(3, files.Length);
        Assert.All(files, file => Assert.Equal("{\"n\":1}\n", File.ReadAllText(file)));
        using var checksums = JsonDocument.Parse(File.ReadAllText(Path.Combine(writer.DirectoryPath, "checksums.json")));
        Assert.Equal(3, checksums.RootElement.EnumerateObject().Count());
        Assert.False(writer.TryEnqueue(new byte[MeasurementFileWriter.MaximumRecordBytes + 1]));
    }

    [Fact]
    public async Task ClosedWindowCanBeReadBeforeFinalization()
    {
        var writer = new MeasurementFileWriter(_root, Guid.NewGuid());
        var running = writer.RunAsync();
        writer.TryEnqueue(Encoding.UTF8.GetBytes("{}"));
        var until = DateTime.UtcNow.AddSeconds(5);
        string? file;
        while ((file = Directory.GetFiles(writer.DirectoryPath, "*.jsonl").FirstOrDefault()) is null || new FileInfo(file).Length < 3)
        {
            Assert.True(DateTime.UtcNow < until);
            await Task.Delay(10);
        }
        try
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(input);
            Assert.Equal("{}\n", reader.ReadToEnd());
            Assert.False(running.IsCompleted);
        }
        finally { writer.Complete(); await running; }
    }

    [Fact]
    public async Task DiskErrorAndSessionLimitAreContained()
    {
        Directory.CreateDirectory(_root);
        var blocked = Path.Combine(_root, "file-instead-of-directory");
        File.WriteAllText(blocked, "fixture");
        var broken = new MeasurementFileWriter(blocked, Guid.NewGuid());
        broken.TryEnqueue(Encoding.UTF8.GetBytes("{}"));
        broken.Complete();
        await broken.RunAsync();
        Assert.True(broken.Failed);
        Assert.False(broken.TryEnqueue(Encoding.UTF8.GetBytes("{}")));
        var limited = new MeasurementFileWriter(_root, Guid.NewGuid(), sessionBytes: 4);
        limited.TryEnqueue(Encoding.UTF8.GetBytes("{}"));
        limited.TryEnqueue(Encoding.UTF8.GetBytes("{}"));
        limited.Complete();
        await limited.RunAsync();
        Assert.True(limited.Failed);
        Assert.Equal("{}\n", File.ReadAllText(Directory.GetFiles(limited.DirectoryPath, "*.jsonl").Single()));
    }

    [Fact]
    public async Task GracefulStopFlushesPartialAndMapsInstanceWithoutSecrets()
    {
        using var host = new MeasurementHost(new() { Enabled = true, SessionId = Guid.NewGuid(), OutputDirectory = _root }, "worker", "synthetic-worker");
        await host.StartAsync(default);
        PerformanceMetrics.Count(Event.TerminalApplied, Outcome.Sent, 7);
        await host.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        var directory = Directory.GetDirectories(Path.Combine(_root, "processes")).Single();
        Assert.True(File.Exists(Path.Combine(directory, "stopped.json")));
        using var interval = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(directory, "intervals-*.jsonl").Single()));
        Assert.Equal("partial", interval.RootElement.GetProperty("quality").GetString());
        Assert.Equal(7, interval.RootElement.GetProperty("metrics").GetProperty("terminal.sent.delta").GetProperty("value").GetInt64());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
