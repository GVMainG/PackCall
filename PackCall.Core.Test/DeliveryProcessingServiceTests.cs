using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Services;

namespace PackCall.Core.Test;

public class DeliveryProcessingServiceTests
{
    private readonly FakeCampaignRepository _campaignRepository = new();
    private readonly FakeDeliveryRepository _deliveryRepository = new();
    private readonly FakeMessageSender _sender = new();
    private readonly IDeliveryProcessingService _service;

    public DeliveryProcessingServiceTests() =>
        _service = new DeliveryProcessingService(
            _deliveryRepository, _campaignRepository, _sender, new FakeJournal(), new FakeMetrics());

    private Campaign CreateStartedCampaign()
    {
        var campaign = Campaign.Create("Кампания", "Текст кампании");
        campaign.StatusFromDraftToInProgress();
        _campaignRepository.Create(campaign, CancellationToken.None).GetAwaiter().GetResult();
        return campaign;
    }

    private Delivery AddWaitingDelivery(Campaign campaign)
    {
        var delivery = Delivery.Create(campaign.Id, Guid.NewGuid(), "user@example.com", DateTime.UtcNow);
        _deliveryRepository.Add(delivery);
        return delivery;
    }

    [Fact]
    public async Task Process_WhenDeliveryIsNotInProgress_DoesNotSend()
    {
        var delivery = AddWaitingDelivery(CreateStartedCampaign());

        await Assert.ThrowsAsync<DomainException>(() => _service.ProcessAsync(delivery, CancellationToken.None));

        Assert.Empty(_sender.Requests);
        Assert.Equal(DeliveryStatus.Waiting, delivery.DeliveryStatus);
    }

    [Fact]
    public async Task Process_WhenCampaignIsDraft_DoesNotSend()
    {
        var campaign = Campaign.Create("Кампания", "Текст");
        await _campaignRepository.Create(campaign, CancellationToken.None);
        var delivery = AddWaitingDelivery(campaign);
        delivery.StatusFromWaitingToInProgress("worker-1", DateTime.UtcNow.AddMinutes(2));

        await Assert.ThrowsAsync<DomainException>(() => _service.ProcessAsync(delivery, CancellationToken.None));

        Assert.Empty(_sender.Requests);
        Assert.Equal(DeliveryStatus.InProgress, delivery.DeliveryStatus);
    }

    [Fact]
    public async Task Process_WhenOperatorThrows_PreservesLockForRecovery()
    {
        var campaign = CreateStartedCampaign();
        var delivery = AddWaitingDelivery(campaign);
        var expiresAt = DateTime.UtcNow.AddMinutes(2);
        delivery.StatusFromWaitingToInProgress("worker-1", expiresAt);
        _sender.Handler = _ => throw new InvalidOperationException("Ответ оператора неизвестен");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ProcessAsync(delivery, CancellationToken.None));

        Assert.Equal(DeliveryStatus.InProgress, delivery.DeliveryStatus);
        Assert.Equal("worker-1", delivery.WorkerId);
        Assert.Equal(expiresAt, delivery.LockExpiresAt);
        Assert.Null(delivery.CompletedAt);
    }

    [Fact]
    public async Task ReleaseExpiredLocks_ReturnsOnlyExpiredToWaiting()
    {
        var campaign = CreateStartedCampaign();
        var expired = AddWaitingDelivery(campaign);
        var active = AddWaitingDelivery(campaign);
        var now = DateTime.UtcNow;
        expired.StatusFromWaitingToInProgress("worker-dead", now.AddSeconds(-5), now);
        active.StatusFromWaitingToInProgress("worker-alive", now.AddMinutes(5), now);

        var released = await _service.ReleaseExpiredLocksAsync(CancellationToken.None);

        Assert.Equal(1, released);
        Assert.Equal(DeliveryStatus.Waiting, expired.DeliveryStatus);
        Assert.Equal(DeliveryStatus.InProgress, active.DeliveryStatus);
    }
}
