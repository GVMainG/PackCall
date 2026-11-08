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
        _clock.Advance(utcSeconds: 5, elapsedSeconds: 5);

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
        private DateTimeOffset _utc = DateTimeOffset.UnixEpoch;
        private long _timestamp;

        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;

        public void Advance(int utcSeconds, int elapsedSeconds)
        {
            _utc = _utc.AddSeconds(utcSeconds);
            _timestamp += elapsedSeconds;
        }
    }
}
