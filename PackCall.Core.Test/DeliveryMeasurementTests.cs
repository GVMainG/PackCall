using PackCall.Core.Diagnostics;
using PackCall.Core.Entities;
using PackCall.Core.Journaling;
using PackCall.Core.Services;
using PackCall.Core.Sending;
using PackCall.Infrastructure.Sending;

namespace PackCall.Core.Test;

[Collection("Measurement")]
public sealed class DeliveryMeasurementTests
{
    [Theory]
    [InlineData(true, true, false, 1, Outcome.TerminalPathComplete)]
    [InlineData(false, true, false, 1, Outcome.TerminalPathComplete)]
    [InlineData(true, false, false, 0, Outcome.StaleResult)]
    [InlineData(true, true, true, 1, Outcome.Error)]
    public async Task CountsConfirmationsAndFullPathSeparately(bool accepted, bool applied, bool journalFails, int terminal, Outcome finished)
    {
        using var collector = Collector();
        var fixture = await Create(accepted);
        fixture.Deliveries.CompleteHandler = () => Task.FromResult(applied);
        fixture.Journal.Fail = journalFails;
        if (journalFails) await Assert.ThrowsAsync<IOException>(() => fixture.Service.ProcessAsync(fixture.Delivery, default));
        else await fixture.Service.ProcessAsync(fixture.Delivery, default);
        var window = collector.Close()!;
        Assert.Equal(terminal, Count(window, Event.TerminalApplied, accepted ? Outcome.Sent : Outcome.Failed));
        Assert.Equal(0, Count(window, Event.TerminalApplied, accepted ? Outcome.Failed : Outcome.Sent));
        Assert.Equal(1, Count(window, Event.ProcessingFinished, finished));
        Assert.Equal(1, Count(window, Event.ResultFinished, applied ? Outcome.Applied : Outcome.NotApplied));
        Assert.Equal(journalFails ? 1 : 0, Count(window, Event.AfterTerminalApplied, Outcome.Error));
        Assert.Equal(applied ? 1 : 0, window.Histograms.Keys.Count(k => k.Metric == (int)Operation.JournalAppend));
    }

    [Fact]
    public async Task CancellationAfterTrueRetainsTerminalAndSkipsJournal()
    {
        using var collector = Collector();
        using var cancellation = new CancellationTokenSource();
        var fixture = await Create(true);
        fixture.Deliveries.CompleteHandler = () => { cancellation.Cancel(); return Task.FromResult(true); };
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Service.ProcessAsync(fixture.Delivery, cancellation.Token));
        var window = collector.Close()!;
        Assert.Equal(1, Count(window, Event.TerminalApplied, Outcome.Sent));
        Assert.Equal(1, Count(window, Event.AfterTerminalApplied, Outcome.Cancelled));
        Assert.Equal(0, Count(window, Event.ProcessingFinished, Outcome.TerminalPathComplete));
        Assert.Empty(fixture.Journal.Entries);
    }

    [Theory]
    [InlineData(Outcome.Timeout)] [InlineData(Outcome.Transport)] [InlineData(Outcome.Protocol)]
    [InlineData(Outcome.Validation)] [InlineData(Outcome.Service)] [InlineData(Outcome.Cancelled)] [InlineData(Outcome.Error)]
    public async Task OperatorExceptionsHaveBoundedCategoriesAndNoTerminal(Outcome expected)
    {
        using var collector = Collector();
        var fixture = await Create(true);
        Exception exception = expected switch
        {
            Outcome.Timeout => new TestOperatorTimeoutException(),
            Outcome.Transport => new TestOperatorTransportException(),
            Outcome.Protocol => new TestOperatorProtocolException(),
            Outcome.Validation => new TestOperatorValidationException(),
            Outcome.Service => new TestOperatorServiceException(),
            Outcome.Cancelled => new OperationCanceledException(),
            _ => new Exception("secret-canary")
        };
        fixture.Sender.Handler = _ => throw exception;
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.ProcessAsync(fixture.Delivery, default));
        Assert.Same(exception, thrown);
        var window = collector.Close()!;
        Assert.Equal(1, Count(window, Event.OperatorFinished, expected));
        Assert.Equal(0, Count(window, Event.TerminalApplied, Outcome.Sent));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnknownPersistenceOutcomeDoesNotInventCommit(bool cancelled)
    {
        using var collector = Collector();
        var fixture = await Create(true);
        fixture.Deliveries.CompleteHandler = () => throw (cancelled ? new OperationCanceledException() : new IOException("unknown commit"));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.ProcessAsync(fixture.Delivery, default));
        var window = collector.Close()!;
        Assert.Equal(0, Count(window, Event.TerminalApplied, Outcome.Sent));
        Assert.Equal(1, Count(window, Event.ResultFinished, cancelled ? Outcome.Cancelled : Outcome.Error));
    }

    [Fact]
    public async Task JournalDelayBelongsOnlyToJournalAndParentNotOperatorOrPersist()
    {
        using var collector = Collector();
        var time = new Clock();
        var fixture = await Create(true, time);
        fixture.Sender.Handler = _ => { time.Advance(100); return SendResult.Success(); };
        fixture.Deliveries.CompleteHandler = () => { time.Advance(200); return Task.FromResult(true); };
        fixture.Journal.OnAppend = () => time.Advance(300);
        fixture.Campaigns.OnGet = () => time.Advance(400);
        fixture.Campaigns.OnTryUpdateStatus = () => time.Advance(500);
        await fixture.Service.ProcessAsync(fixture.Delivery, default);
        var window = collector.Close()!;
        Assert.Equal(100, Sum(window, Operation.OperatorCall));
        Assert.Equal(200, Sum(window, Operation.ResultPersist));
        Assert.Equal(300, Sum(window, Operation.JournalAppend));
        Assert.Equal(900, Sum(window, Operation.CampaignCheck));
        Assert.Equal(500, Sum(window, Operation.CampaignPersist));
        Assert.Equal(1, Count(window, Event.CampaignCompleted));
        await fixture.Service.CompleteCampaignIfFinishedAsync(fixture.Delivery.CampaignId, default);
        Assert.Equal(0, Count(collector.Close()!, Event.CampaignCompleted));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PauseReturnIsNotTerminal(bool missingOwner)
    {
        using var collector = Collector();
        var fixture = await Create(true);
        var campaign = (await fixture.Campaigns.Get(fixture.Delivery.CampaignId, default)).Single();
        campaign.StatusFromInProgressToPaused(DateTime.UtcNow);
        if (missingOwner) fixture.Delivery.SetCapturedForTest(null!, DateTime.UtcNow.AddMinutes(1));
        await fixture.Service.ProcessAsync(fixture.Delivery, default);
        var window = collector.Close()!;
        Assert.Equal(1, Count(window, Event.ProcessingFinished, missingOwner ? Outcome.PausedNoRelease : Outcome.PausedRelease));
        Assert.Equal(0, Count(window, Event.TerminalApplied, Outcome.Sent));
        Assert.Empty(fixture.Sender.Requests);
    }

    private static double Sum(MeasurementWindow window, Operation operation) => window.Histograms.Where(x => x.Key.Metric == (int)operation).Sum(x => x.Value.Sum);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task StorageRowsRemainObservedWhenCaptureOrRecoveryJournalFails(bool recovery)
    {
        using var collector = Collector();
        var fixture = await Create(true);
        fixture.Journal.Fail = true;
        if (recovery)
        {
            fixture.Delivery.SetCapturedForTest("previous-owner", DateTime.UtcNow.AddMinutes(-1));
            await Assert.ThrowsAsync<IOException>(() => fixture.Service.ReleaseExpiredLocksAsync(default));
        }
        else
        {
            fixture.Delivery.SetWaitingForTest(DateTime.UtcNow);
            await Assert.ThrowsAsync<IOException>(() => fixture.Service.CaptureBatchAsync(10, "worker", TimeSpan.FromSeconds(90), default));
        }
        var window = collector.Close()!;
        Assert.Equal(1, Count(window, recovery ? Event.RecoveryRows : Event.CaptureRows));
        Assert.Equal(1, Count(window, recovery ? Event.RecoveryCalls : Event.CaptureCalls));
        Assert.Equal(0, Count(window, Event.CaptureEmpty));
        Assert.Equal(1, window.Histograms[new((int)(recovery ? Operation.RecoveryStorage : Operation.CaptureStorage), Outcome.Success, Purpose.None)].Count);
        Assert.Equal(1, window.Histograms[new((int)(recovery ? Operation.RecoveryTotal : Operation.CaptureTotal), Outcome.Error, Purpose.None)].Count);
    }
    private static long Count(MeasurementWindow window, Event metric, Outcome outcome = Outcome.Success) =>
        window.Counters.GetValueOrDefault(new((int)metric, outcome, Purpose.None))?.Delta ?? 0;
    private static MeasurementCollector Collector() => new(new() { Enabled = true, SessionId = Guid.NewGuid(), OutputDirectory = "unused" });

    private static async Task<Fixture> Create(bool accepted, TimeProvider? time = null)
    {
        var campaigns = new FakeCampaignRepository();
        var deliveries = new FakeDeliveryRepository();
        var sender = new FakeMessageSender { Handler = _ => accepted ? SendResult.Success() : SendResult.Failure("SimulatedOperatorRejected") };
        var journal = new Journal();
        var campaign = Campaign.Create("synthetic", "synthetic");
        campaign.StatusFromDraftToInProgress();
        await campaigns.Create(campaign, default);
        var delivery = Delivery.Create(campaign.Id, Guid.NewGuid(), "synthetic@example.invalid", DateTime.UtcNow);
        deliveries.Add(delivery);
        delivery.SetCapturedForTest("worker", DateTime.UtcNow.AddMinutes(2));
        return new(new(deliveries, campaigns, sender, journal, new(false), time), deliveries, sender, journal, delivery, campaigns);
    }

    private sealed record Fixture(DeliveryProcessingService Service, FakeDeliveryRepository Deliveries, FakeMessageSender Sender, Journal Journal, Delivery Delivery, FakeCampaignRepository Campaigns);
    private sealed class Journal : IDeliveryStatusJournal
    {
        public bool Fail;
        public Action? OnAppend;
        public List<StatusChangeEntry> Entries = [];
        public Task AppendAsync(StatusChangeEntry entry, CancellationToken ct)
        {
            OnAppend?.Invoke();
            if (Fail) throw new IOException("secret-canary");
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += milliseconds;
    }
}
