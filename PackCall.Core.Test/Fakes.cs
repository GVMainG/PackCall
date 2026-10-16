using PackCall.Core.Entities;
using PackCall.Core.Journaling;
using PackCall.Core.Metrics;
using PackCall.Core.Repositorys;
using PackCall.Core.Sending;
using System.Linq.Expressions;

namespace PackCall.Core.Test;

internal sealed class FakeCampaignRepository : ICampaingRepository
{
    private readonly Dictionary<Guid, Campaign> _store = new();
    private readonly FakeDeliveryRepository? _deliveries;

    public FakeCampaignRepository(FakeDeliveryRepository? deliveries = null)
    {
        _deliveries = deliveries;
    }

    public int CreateCount { get; private set; }
    public int UpdateCount { get; private set; }

    public Task<IEnumerable<Campaign>> Get(Guid id, CancellationToken ct) =>
        Task.FromResult(_store.TryGetValue(id, out var campaign)
            ? new[] { campaign }.AsEnumerable()
            : Enumerable.Empty<Campaign>());

    public Task<IEnumerable<Campaign>> Get(Expression<Func<Campaign, bool>> predicate, CancellationToken ct) =>
        Task.FromResult(_store.Values.AsQueryable().Where(predicate).ToList().AsEnumerable());

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
        foreach (var delivery in campaign.Deliveries)
            _deliveries?.Add(delivery);
        CreateCount++;
        return Task.FromResult(campaign);
    }

    public Task<Campaign> Update(Campaign campaign, CancellationToken ct)
    {
        if (!_store.ContainsKey(campaign.Id))
            throw new KeyNotFoundException($"Кампания {campaign.Id} не найдена.");
        _store[campaign.Id] = campaign;
        UpdateCount++;
        return Task.FromResult(campaign);
    }

    public Task<bool> Delete(Guid id, CancellationToken ct)
    {
        if (!_store.Remove(id))
            return Task.FromResult(false);
        _deliveries?.RemoveByCampaign(id);
        return Task.FromResult(true);
    }
}

internal sealed class FakeRecipientRepository : IRecipientRepository
{
    public List<Recipient> Items { get; } = new();

    public Task<IReadOnlyList<Recipient>> GetBatchAsync(int offset, int batchSize, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Recipient>>(Items.DistinctBy(recipient => recipient.Id)
            .OrderBy(recipient => recipient.Id).Skip(offset).Take(batchSize).ToList());

    public Task<Recipient?> GetAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(recipient => recipient.Id == id));

    public Task<PageResult<Recipient>> GetPageAsync(int pageNumber, int pageSize, CancellationToken ct) =>
        Task.FromResult(new PageResult<Recipient>(
            Items.OrderBy(recipient => recipient.Id).Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList(),
            Items.Count));
}

internal sealed class FakeDeliveryRepository : IDeliveryRepository
{
    private readonly List<Delivery> _store = new();

    public FakeDeliveryRepository()
    {
    }

    public IReadOnlyList<Delivery> Items => _store;

    public Func<IReadOnlyList<Delivery>, Task>? BeforeAddBatchAsync { get; set; }

    public async Task AddBatchAsync(IReadOnlyList<Delivery> deliveries, CancellationToken ct)
    {
        if (BeforeAddBatchAsync is not null)
            await BeforeAddBatchAsync(deliveries);
        _store.AddRange(deliveries);
    }

    public void Add(Delivery delivery) => _store.Add(delivery);

    public void RemoveByCampaign(Guid campaignId) =>
        _store.RemoveAll(delivery => delivery.CampaignId == campaignId);

    public Task<IEnumerable<Delivery>> Get(Guid id, CancellationToken ct) =>
        Task.FromResult(_store.Where(d => d.Id == id).ToList().AsEnumerable());

    public Task<IEnumerable<Delivery>> Get(Guid campaignId, Expression<Func<Delivery, bool>> predicate, CancellationToken ct) =>
        Task.FromResult(_store.AsQueryable().Where(d => d.CampaignId == campaignId).Where(predicate).ToList().AsEnumerable());

    public Task<PageResult<Delivery>> GetPage(Guid campaignId, int pageNumber, int pageSize, CancellationToken ct)
    {
        var deliveries = _store.Where(d => d.CampaignId == campaignId).OrderBy(d => d.Id).ToList();
        var items = deliveries.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PageResult<Delivery>(items, deliveries.Count));
    }

    public Task<DeliveryStatusCounts> GetStatusCountsAsync(Guid campaignId, CancellationToken ct)
    {
        var deliveries = _store.Where(delivery => delivery.CampaignId == campaignId).ToList();
        return Task.FromResult(new DeliveryStatusCounts(deliveries.Count,
            deliveries.Count(delivery => delivery.DeliveryStatus == DeliveryStatus.Waiting),
            deliveries.Count(delivery => delivery.DeliveryStatus == DeliveryStatus.InProgress),
            deliveries.Count(delivery => delivery.DeliveryStatus == DeliveryStatus.Sent),
            deliveries.Count(delivery => delivery.DeliveryStatus == DeliveryStatus.Failed)));
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
            if (delivery.Campaign is not null && delivery.Campaign.Status != CampaignStatus.InProgress)
                continue;

            delivery.StatusFromWaitingToInProgress(workerId, now + lockDuration, now);
            captured.Add(delivery);
        }

        return Task.FromResult<IReadOnlyList<Delivery>>(captured);
    }

    public Task<IReadOnlyList<Delivery>> ReleaseExpiredLocksAsync(DateTime now, CancellationToken ct)
    {
        var released = _store.Where(d => d.StatusFromInProgressToWaiting(now)).ToList();
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
    public Task AppendAsync(StatusChangeEntry entry, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeMetrics : IDeliveryMetrics
{
    public void RecordCaptured(int count) { }
    public void RecordProcessed() { }

    public void RecordStatusChange(DeliveryStatus previous, DeliveryStatus current)
    {
    }

    public void RecordExpiredLockReturned(int count) { }
    public void RecordOperatorRetry() { }
    public void RecordOperatorDuplicateSuppressed() { }
    public void RecordOperatorSuccess() { }
    public void RecordOperatorError() { }
}
