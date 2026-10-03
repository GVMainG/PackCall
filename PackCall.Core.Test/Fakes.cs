using PackCall.Core.Entities;
using PackCall.Core.Journaling;
using PackCall.Core.Metrics;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;

namespace PackCall.Core.Test;

internal sealed class FakeCampaignRepository : ICampaingRepository
{
    private readonly Dictionary<Guid, Campaign> _store = new();

    public FakeCampaignRepository()
    {
    }

    public Task<IEnumerable<Campaign>> Get(Guid id, CancellationToken ct) =>
        Task.FromResult(_store.TryGetValue(id, out var campaign)
            ? new[] { campaign }.AsEnumerable()
            : Enumerable.Empty<Campaign>());

    public Task<IEnumerable<Campaign>> Get(Func<Campaign, bool> predicate, CancellationToken ct) =>
        Task.FromResult(_store.Values.Where(predicate).ToList().AsEnumerable());

    public Task<PageResult<Campaign>> GetPage(int pageNumber, int pageSize, CancellationToken ct)
    {
        var items = _store.Values
            .OrderBy(c => c.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return Task.FromResult(new PageResult<Campaign>(items, _store.Count));
    }

    public Task<Campaign> Create(Campaign campaign, CancellationToken ct)
    {
        _store[campaign.Id] = campaign;
        return Task.FromResult(campaign);
    }

    public Task<Campaign> Update(Campaign campaign, CancellationToken ct)
    {
        if (!_store.ContainsKey(campaign.Id))
            throw new KeyNotFoundException($"Кампания {campaign.Id} не найдена.");
        _store[campaign.Id] = campaign;
        return Task.FromResult(campaign);
    }

    public Task<bool> Delete(Guid id, CancellationToken ct) =>
        Task.FromResult(_store.Remove(id));
}

internal sealed class FakeDeliveryRepository : IDeliveryRepository
{
    private readonly List<Delivery> _store = new();

    public FakeDeliveryRepository()
    {
    }

    public IReadOnlyList<Delivery> Items => _store;

    public void Add(Delivery delivery) => _store.Add(delivery);

    public Task<IEnumerable<Delivery>> Get(Guid id, CancellationToken ct) =>
        Task.FromResult(_store.Where(d => d.Id == id).ToList().AsEnumerable());

    public Task<IEnumerable<Delivery>> Get(Guid campaignId, Func<Delivery, bool> predicate, CancellationToken ct) =>
        Task.FromResult(_store.Where(d => d.CampaignId == campaignId && predicate(d)).ToList().AsEnumerable());

    public Task<PageResult<Delivery>> GetPage(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct)
    {
        var deliveries = _store.Where(d => d.CampaignId == campaignId).OrderBy(d => d.Id).ToList();
        var items = deliveries.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PageResult<Delivery>(items, deliveries.Count));
    }

    public Task<IReadOnlyList<Delivery>> CaptureBatchAsync(
        int batchSize, string workerId, TimeSpan lockDuration, DateTime now, CancellationToken ct)
    {
        var captured = new List<Delivery>();
        foreach (var delivery in _store)
        {
            if (captured.Count >= batchSize)
                break;
            if (delivery.DeliveryStatus != DeliveryStatus.Waiting)
                continue;

            delivery.Capture(workerId, now + lockDuration);
            captured.Add(delivery);
        }

        return Task.FromResult<IReadOnlyList<Delivery>>(captured);
    }

    public Task<IReadOnlyList<Delivery>> ReleaseExpiredLocksAsync(DateTime now, CancellationToken ct)
    {
        var released = _store.Where(d => d.TryReturnIfLockExpired(now)).ToList();
        return Task.FromResult<IReadOnlyList<Delivery>>(released);
    }

    public Task SaveAsync(Delivery delivery, CancellationToken ct) => Task.CompletedTask;

    public Task<bool> HasPendingDeliveriesAsync(Guid campaignId, CancellationToken ct) =>
        Task.FromResult(_store.Any(d => d.CampaignId == campaignId &&
            d.DeliveryStatus is DeliveryStatus.Waiting or DeliveryStatus.InProgress));
}

internal sealed class FakeMessageSender : IMessageSender
{
    public Func<SendRequest, SendResult>? Handler { get; set; }

    public List<SendRequest> Requests { get; } = new();

    public Task<SendResult> SendAsync(SendRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Handler?.Invoke(request) ?? SendResult.Success());
    }
}

internal sealed class FakeJournal : IDeliveryStatusJournal
{
    public List<StatusChangeEntry> Entries { get; } = new();

    public Task AppendAsync(StatusChangeEntry entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class FakeMetrics : IDeliveryMetrics
{
    public int Captured { get; private set; }
    public int Processed { get; private set; }
    public int ExpiredLockReturned { get; private set; }
    public int OperatorRetries { get; private set; }
    public int OperatorDuplicateSuppressed { get; private set; }
    public int OperatorSuccess { get; private set; }
    public int OperatorErrors { get; private set; }

    public void RecordCaptured(int count) => Captured += count;

    public void RecordProcessed() => Processed++;

    public void RecordStatusChange(DeliveryStatus previous, DeliveryStatus current)
    {
    }

    public void RecordExpiredLockReturned(int count) => ExpiredLockReturned += count;

    public void RecordOperatorRetry() => OperatorRetries++;

    public void RecordOperatorDuplicateSuppressed() => OperatorDuplicateSuppressed++;

    public void RecordOperatorSuccess() => OperatorSuccess++;

    public void RecordOperatorError() => OperatorErrors++;
}
