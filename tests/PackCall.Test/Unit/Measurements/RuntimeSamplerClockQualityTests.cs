using System.Text.Json;
using PackCall.Core.Diagnostics;

namespace PackCall.Core.Test;

[Trait("Category", "Unit")]
[Trait("Module", "Measurements")]
public sealed class RuntimeSamplerClockQualityTests
{
    private readonly Clock _clock = new();
    private readonly RuntimeSampler _sampler;

    public RuntimeSamplerClockQualityTests() => _sampler = new(Guid.NewGuid(), Guid.NewGuid(), _clock);

    [Fact]
    public void FirstCollectIsComplete()
    {
        Assert.Equal("complete", CollectQuality());
    }

    [Fact]
    public void AdvancingClocksKeepTheNextCollectComplete()
    {
        CollectQuality();
        _clock.Advance(utcSeconds: 0, elapsedSeconds: 5);
        var startUtc = DateTimeOffset.UnixEpoch.AddSeconds(5);
        _clock.SetUtcReadings(startUtc, startUtc.AddSeconds(1), startUtc.AddSeconds(2));

        Assert.Equal("complete", CollectQuality());
    }

    [Fact]
    public void EqualClockReadingsKeepTheNextCollectComplete()
    {
        CollectQuality();

        Assert.Equal("complete", CollectQuality());
    }

    [Fact]
    public void BackwardUtcJumpMarksClockJump()
    {
        CollectQuality();
        _clock.Advance(utcSeconds: -5, elapsedSeconds: 5);

        Assert.Equal("clock_jump", CollectQuality());

        _clock.Advance(utcSeconds: 1, elapsedSeconds: 1);
        Assert.Equal("complete", CollectQuality());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackwardUtcJumpWithinCollectMarksClockJumpAndPreservesObservedEnd(bool hasPreviousSample)
    {
        if (hasPreviousSample)
            Assert.Equal("complete", CollectQuality());

        var startUtc = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var endUtc = startUtc.AddSeconds(-5);
        var receiptUtc = startUtc.AddSeconds(1);
        _clock.Advance(utcSeconds: 0, elapsedSeconds: 10);
        _clock.SetUtcReadings(startUtc, endUtc, receiptUtc);

        using var sample = JsonDocument.Parse(_sampler.Collect());
        Assert.Equal("clock_jump", sample.RootElement.GetProperty("quality").GetString());
        Assert.Equal(startUtc, sample.RootElement.GetProperty("start_utc").GetDateTimeOffset());
        Assert.Equal(endUtc, sample.RootElement.GetProperty("end_utc").GetDateTimeOffset());
        Assert.Equal(receiptUtc, sample.RootElement.GetProperty("receipt_utc").GetDateTimeOffset());

        var nextStartUtc = startUtc.AddSeconds(-1);
        var nextEndUtc = startUtc.AddSeconds(2);
        _clock.Advance(utcSeconds: 0, elapsedSeconds: 5);
        _clock.SetUtcReadings(nextStartUtc, nextEndUtc, nextEndUtc.AddSeconds(1));

        Assert.Equal("clock_jump", CollectQuality());
    }

    [Fact]
    public void RegressingMonotonicElapsedMarksPartial()
    {
        _clock.Advance(utcSeconds: 10, elapsedSeconds: 10);
        CollectQuality();
        _clock.Advance(utcSeconds: 5, elapsedSeconds: -5);

        Assert.Equal("partial", CollectQuality());

        _clock.Advance(utcSeconds: 1, elapsedSeconds: 1);
        Assert.Equal("complete", CollectQuality());
    }

    [Fact]
    public void BackwardUtcJumpTakesPrecedenceOverMonotonicRegression()
    {
        _clock.Advance(utcSeconds: 10, elapsedSeconds: 10);
        CollectQuality();
        _clock.Advance(utcSeconds: -5, elapsedSeconds: -5);

        Assert.Equal("clock_jump", CollectQuality());
    }

    private string? CollectQuality()
    {
        using var sample = JsonDocument.Parse(_sampler.Collect());
        return sample.RootElement.GetProperty("quality").GetString();
    }

    private sealed class Clock : TimeProvider
    {
        private Queue<DateTimeOffset> _utcReadings = new();
        private DateTimeOffset _utc = DateTimeOffset.UnixEpoch;
        private long _timestamp;

        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utcReadings.TryDequeue(out var utc) ? utc : _utc;

        public void SetUtcReadings(params DateTimeOffset[] readings) => _utcReadings = new(readings);

        public void Advance(int utcSeconds, int elapsedSeconds)
        {
            _utc = _utc.AddSeconds(utcSeconds);
            _timestamp += elapsedSeconds;
        }
    }
}
