using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace PackCall.Core.Diagnostics;

/// <summary>Bounded single-consumer writer. All disk activity is off the business path.</summary>
public sealed class MeasurementFileWriter
{
    public const int MaximumRecordBytes = 1024 * 1024;
    public const long DefaultRotationBytes = 32 * 1024 * 1024;
    public const long DefaultSessionBytes = 2L * 1024 * 1024 * 1024;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly string _root, _directory;
    private readonly long _rotationBytes, _sessionBytes;
    private readonly string _prefix;
    private readonly int _maximumRecordBytes;
    private readonly Dictionary<string, string> _checksums = new();
    private long _dropped;
    private int _failed;
    public long Dropped => Interlocked.Read(ref _dropped);
    public bool Failed => Volatile.Read(ref _failed) != 0;
    public string DirectoryPath => _directory;

    public MeasurementFileWriter(string root, Guid instanceId, long rotationBytes = DefaultRotationBytes, long sessionBytes = DefaultSessionBytes,
        string prefix = "intervals", int maximumRecordBytes = MaximumRecordBytes)
    {
        _root = Path.GetFullPath(root);
        _directory = Path.Combine(_root, "processes", instanceId.ToString());
        _rotationBytes = rotationBytes;
        _sessionBytes = sessionBytes;
        if (prefix is not ("intervals" or "runtime")) throw new ArgumentException("Invalid measurement stream.");
        _prefix = prefix;
        _maximumRecordBytes = Math.Min(maximumRecordBytes, MaximumRecordBytes);
    }

    public bool TryEnqueue(byte[] record)
    {
        if (Failed || record.Length > _maximumRecordBytes || !_queue.Writer.TryWrite(record))
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }
        return true;
    }

    public void Complete() => _queue.Writer.TryComplete();

    public async Task RunAsync(CancellationToken ct = default)
    {
        FileStream? stream = null;
        string? path = null;
        var part = 0;
        try
        {
            Directory.CreateDirectory(_directory);
            await foreach (var record in _queue.Reader.ReadAllAsync(ct))
            {
                if (stream is null || stream.Length + record.Length + 1 > _rotationBytes)
                {
                    if (stream is not null)
                    {
                        await stream.DisposeAsync();
                        Seal(path!);
                    }
                    path = Path.Combine(_directory, $"{_prefix}-{part++:D6}.jsonl");
                    stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16384, FileOptions.Asynchronous);
                }
                if (!Reserve(record.Length + 1)) throw new IOException("measurement_session_limit");
                await stream.WriteAsync(record, ct);
                await stream.WriteAsync(new byte[] { 10 }, ct);
                await stream.FlushAsync(ct); // OS-cache durability only; deliberately not fsync.
            }
            if (stream is not null)
            {
                await stream.DisposeAsync();
                stream = null;
                Seal(path!);
            }
        }
        catch
        {
            Volatile.Write(ref _failed, 1);
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
        }
        finally
        {
            if (stream is not null) try { await stream.DisposeAsync(); } catch { Volatile.Write(ref _failed, 1); }
        }
    }

    // Per-session cross-process reservation. Failure to obtain the lock loses telemetry,
    // never blocks delivery processing and never deletes earlier files.
    private bool Reserve(int bytes)
    {
        FileStream? acquired = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                acquired = new FileStream(Path.Combine(_root, ".byte-budget"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException) when (attempt < 19) { Thread.Sleep(5); }
        }
        using var budget = acquired!;
        Span<byte> stored = stackalloc byte[sizeof(long)];
        var read = budget.Read(stored);
        if (read is not (0 or sizeof(long))) throw new IOException("invalid_byte_budget");
        var total = read == sizeof(long) ? BitConverter.ToInt64(stored) : 0;
        if (total > _sessionBytes - bytes) return false;
        budget.Position = 0;
        budget.Write(BitConverter.GetBytes(total + bytes));
        budget.Flush();
        return true;
    }

    private void Seal(string path)
    {
        using var input = File.OpenRead(path);
        _checksums[Path.GetFileName(path)] = Convert.ToHexStringLower(SHA256.HashData(input));
        AtomicWrite(Path.Combine(_directory, _prefix == "intervals" ? "checksums.json" : "runtime-checksums.json"), JsonSerializer.SerializeToUtf8Bytes(_checksums));
    }

    public static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
