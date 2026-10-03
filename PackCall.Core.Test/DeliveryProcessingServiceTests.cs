using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Services;
using PackCall.Core.Sending;

namespace PackCall.Core.Test;

public class DeliveryProcessingServiceTests
{
    private readonly FakeCampaignRepository _campaignRepository = new();
    private readonly FakeDeliveryRepository _deliveryRepository = new();
    private readonly FakeMessageSender _sender = new();
    private readonly FakeJournal _journal = new();
    private readonly FakeMetrics _metrics = new();
    private readonly DeliveryProcessingService _service;

    public DeliveryProcessingServiceTests() =>
        _service = new DeliveryProcessingService(
            _deliveryRepository, _campaignRepository, _sender, _journal, _metrics);

    private Campaign CreateStartedCampaign()
    {
        var campaign = Campaign.Create("Кампания", "Текст кампании");
        campaign.ToInProgress();
        _campaignRepository.Create(campaign, CancellationToken.None).GetAwaiter().GetResult();
        return campaign;
    }

    private Delivery AddWaitingDelivery(Campaign campaign, string email = "user@example.com")
    {
        var delivery = Delivery.Create(campaign.Id, Guid.NewGuid(), email, DateTime.UtcNow);
        _deliveryRepository.Add(delivery);
        return delivery;
    }

    private Campaign GetStoredCampaign(Guid campaignId) =>
        _campaignRepository.Get(campaignId, CancellationToken.None).GetAwaiter().GetResult().Single();

    [Fact]
    public async Task CaptureBatch_CapturesOnlyWaitingDeliveries()
    {
        var campaign = CreateStartedCampaign();
        var waiting1 = AddWaitingDelivery(campaign);
        var waiting2 = AddWaitingDelivery(campaign);
        var alreadyInProgress = AddWaitingDelivery(campaign);
        alreadyInProgress.Capture("worker-other", DateTime.UtcNow.AddMinutes(5));

        var captured = await _service.CaptureBatchAsync(10, "worker-1", TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Equal(2, captured.Count);
        Assert.All(captured, d =>
        {
            Assert.Equal(DeliveryStatus.InProgress, d.DeliveryStatus);
            Assert.Equal("worker-1", d.WorkerId);
            Assert.NotNull(d.LockExpiresAt);
        });
        Assert.Contains(captured, d => d.Id == waiting1.Id);
        Assert.Contains(captured, d => d.Id == waiting2.Id);
        Assert.Equal(2, _metrics.Captured);
        Assert.Equal(2, _journal.Entries.Count(e =>
            e.PreviousStatus == DeliveryStatus.Waiting && e.NewStatus == DeliveryStatus.InProgress));
    }

    [Fact]
    public async Task CaptureBatch_RespectsBatchSize()
    {
        var campaign = CreateStartedCampaign();
        for (var i = 0; i < 5; i++)
            AddWaitingDelivery(campaign, $"user{i}@example.com");

        var captured = await _service.CaptureBatchAsync(2, "worker-1", TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Equal(2, captured.Count);
        Assert.Equal(3, _deliveryRepository.Items.Count(d => d.DeliveryStatus == DeliveryStatus.Waiting));
    }

    [Fact]
    public async Task Process_OnSuccess_MarksSentAndCompletesCampaign()
    {
        var campaign = CreateStartedCampaign();
        var delivery = AddWaitingDelivery(campaign);
        delivery.Capture("worker-1", DateTime.UtcNow.AddMinutes(2));

        await _service.ProcessAsync(delivery, CancellationToken.None);

        Assert.Equal(DeliveryStatus.Sent, delivery.DeliveryStatus);
        Assert.Null(delivery.WorkerId);
        Assert.Null(delivery.LockExpiresAt);
        Assert.NotNull(delivery.CompletedAt);

        var request = _sender.Requests.Single();
        Assert.Equal(delivery.Id, request.DeliveryId); // ключ идемпотентности
        Assert.Equal(delivery.RecipientEmail, request.RecipientEmail);
        Assert.Equal(campaign.MessageText, request.MessageText);

        Assert.Equal(1, _metrics.Processed);
        Assert.Equal(1, _metrics.OperatorSuccess);
        Assert.Contains(_journal.Entries, e =>
            e.DeliveryId == delivery.Id &&
            e.PreviousStatus == DeliveryStatus.InProgress &&
            e.NewStatus == DeliveryStatus.Sent);

        Assert.Equal(CampaignStatus.Completed, GetStoredCampaign(campaign.Id).Status);
    }

    [Fact]
    public async Task Process_OnOperatorFailure_MarksFailed()
    {
        var campaign = CreateStartedCampaign();
        var delivery = AddWaitingDelivery(campaign);
        delivery.Capture("worker-1", DateTime.UtcNow.AddMinutes(2));
        _sender.Handler = _ => SendResult.Failure("оператор недоступен");

        await _service.ProcessAsync(delivery, CancellationToken.None);

        Assert.Equal(DeliveryStatus.Failed, delivery.DeliveryStatus);
        Assert.Equal(1, _metrics.OperatorErrors);
        Assert.Contains(_journal.Entries, e =>
            e.DeliveryId == delivery.Id &&
            e.NewStatus == DeliveryStatus.Failed &&
            e.OperatorResult == "оператор недоступен");
    }

    [Fact]
    public async Task Process_WhenDuplicateSuppressed_RecordsRetryMetrics()
    {
        var campaign = CreateStartedCampaign();
        var delivery = AddWaitingDelivery(campaign);
        delivery.Capture("worker-1", DateTime.UtcNow.AddMinutes(2));
        _sender.Handler = _ => SendResult.Success(isDuplicateSuppressed: true);

        await _service.ProcessAsync(delivery, CancellationToken.None);

        Assert.Equal(DeliveryStatus.Sent, delivery.DeliveryStatus);
        Assert.Equal(1, _metrics.OperatorRetries);
        Assert.Equal(1, _metrics.OperatorDuplicateSuppressed);
    }

    [Fact]
    public async Task Process_WhenDeliveryIsNotInProgress_ThrowsDomainException()
    {
        var campaign = CreateStartedCampaign();
        var delivery = AddWaitingDelivery(campaign);

        await Assert.ThrowsAsync<DomainException>(() => _service.ProcessAsync(delivery, CancellationToken.None));
    }

    [Fact]
    public async Task Process_WithPendingDeliveries_DoesNotCompleteCampaign()
    {
        var campaign = CreateStartedCampaign();
        AddWaitingDelivery(campaign, "user1@example.com"); // останется в ожидании
        var delivery = AddWaitingDelivery(campaign, "user2@example.com");
        delivery.Capture("worker-1", DateTime.UtcNow.AddMinutes(2));

        await _service.ProcessAsync(delivery, CancellationToken.None);

        Assert.Equal(CampaignStatus.InProgress, GetStoredCampaign(campaign.Id).Status);
    }

    [Fact]
    public async Task ReleaseExpiredLocks_ReturnsOnlyExpiredToWaiting()
    {
        var campaign = CreateStartedCampaign();
        var expired = AddWaitingDelivery(campaign);
        var active = AddWaitingDelivery(campaign);

        // Обе захвачены воркером с уже истёкшим сроком закрепления.
        await _deliveryRepository.CaptureBatchAsync(
            2, "worker-dead", TimeSpan.FromSeconds(-5), DateTime.UtcNow, CancellationToken.None);

        // Вторая перезахвачена живым воркером с будущим сроком.
        Assert.True(active.TryReturnIfLockExpired(DateTime.UtcNow));
        active.Capture("worker-alive", DateTime.UtcNow.AddMinutes(5));

        var released = await _service.ReleaseExpiredLocksAsync(CancellationToken.None);

        Assert.Equal(1, released);
        Assert.Equal(DeliveryStatus.Waiting, expired.DeliveryStatus);
        Assert.Equal(DeliveryStatus.InProgress, active.DeliveryStatus);
        Assert.Equal(1, _metrics.ExpiredLockReturned);
        Assert.Contains(_journal.Entries, e =>
            e.DeliveryId == expired.Id &&
            e.PreviousStatus == DeliveryStatus.InProgress &&
            e.NewStatus == DeliveryStatus.Waiting);
    }

    [Fact]
    public async Task CompleteCampaign_WhenNoPendingDeliveries_Completes()
    {
        var campaign = CreateStartedCampaign();
        var delivery = AddWaitingDelivery(campaign);
        delivery.Capture("worker-1", DateTime.UtcNow.AddMinutes(2));
        delivery.MarkSent(DateTime.UtcNow);

        var completed = await _service.CompleteCampaignIfFinishedAsync(campaign.Id, CancellationToken.None);

        Assert.True(completed);
        var stored = GetStoredCampaign(campaign.Id);
        Assert.Equal(CampaignStatus.Completed, stored.Status);
        Assert.NotNull(stored.CompletedAt);
    }

    [Fact]
    public async Task CompleteCampaign_WhenPendingDeliveriesExist_ReturnsFalse()
    {
        var campaign = CreateStartedCampaign();
        AddWaitingDelivery(campaign);

        var completed = await _service.CompleteCampaignIfFinishedAsync(campaign.Id, CancellationToken.None);

        Assert.False(completed);
        Assert.Equal(CampaignStatus.InProgress, GetStoredCampaign(campaign.Id).Status);
    }

    [Fact]
    public async Task CompleteCampaign_MissingCampaign_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _service.CompleteCampaignIfFinishedAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
