using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Sending;
using PackCall.Core.Services;

namespace PackCall.Core.Test;

public class DeliveryProcessingServiceTests
{
    private readonly FakeCampaignRepository _campaignRepository = new();
    private readonly FakeDeliveryRepository _deliveryRepository = new();
    private readonly FakeMessageSender _sender = new();
    private readonly FakeJournal _journal = new();
    private readonly IDeliveryProcessingService _service;

    public DeliveryProcessingServiceTests() =>
        _service = new DeliveryProcessingService(
            _deliveryRepository, _campaignRepository, _sender, _journal);

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
        delivery.SetCapturedForTest("worker-1", DateTime.UtcNow.AddMinutes(2));

        await Assert.ThrowsAsync<DomainException>(() => _service.ProcessAsync(delivery, CancellationToken.None));

        Assert.Empty(_sender.Requests);
        Assert.Equal(DeliveryStatus.InProgress, delivery.DeliveryStatus);
    }

    [Fact]
    public async Task Process_WaitsForProviderAcknowledgementBeforeMarkingSent()
    {
        var campaign = CreateStartedCampaign();
        var delivery = AddWaitingDelivery(campaign);
        delivery.SetCapturedForTest("worker-1", DateTime.UtcNow.AddMinutes(2));

        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgement = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sender.AsyncHandler = (_, _) =>
        {
            requestReceived.SetResult();
            return acknowledgement.Task;
        };

        var processing = _service.ProcessAsync(delivery, CancellationToken.None);
        await requestReceived.Task;

        Assert.False(processing.IsCompleted);
        Assert.Equal(DeliveryStatus.InProgress, delivery.DeliveryStatus);
        Assert.Null(delivery.CompletedAt);
        Assert.Equal(CampaignStatus.InProgress, campaign.Status);

        acknowledgement.SetResult(SendResult.Success());
        await processing;

        Assert.Equal(DeliveryStatus.Sent, delivery.DeliveryStatus);
        Assert.NotNull(delivery.CompletedAt);
        Assert.Equal(CampaignStatus.Completed, campaign.Status);
    }

    [Fact]
    public async Task Process_WhenOperatorThrows_PreservesLockForRecovery()
    {
        var campaign = CreateStartedCampaign();
        var delivery = AddWaitingDelivery(campaign);
        var expiresAt = DateTime.UtcNow.AddMinutes(2);
        delivery.SetCapturedForTest("worker-1", expiresAt);
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
        expired.SetCapturedForTest("worker-dead", now.AddSeconds(-5), now);
        active.SetCapturedForTest("worker-alive", now.AddMinutes(5), now);

        var released = await _service.ReleaseExpiredLocksAsync(CancellationToken.None);

        Assert.Equal(1, released);
        Assert.Equal(DeliveryStatus.Waiting, expired.DeliveryStatus);
        Assert.Equal(DeliveryStatus.InProgress, active.DeliveryStatus);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("error")]
    [InlineData("cancelled")]
    public async Task Process_UnappliedStorage_DoesNotJournalOrCheckCompletion(string outcome)
    {
        var campaign = CreateStartedCampaign();
        var delivery = AddWaitingDelivery(campaign);
        delivery.SetCapturedForTest("worker-1", DateTime.UtcNow.AddMinutes(2));
        _deliveryRepository.CompleteHandler = () => outcome switch
        {
            "error" => throw new InvalidOperationException("storage failure"),
            "cancelled" => throw new OperationCanceledException(),
            _ => Task.FromResult(false)
        };
        var operation = () => _service.ProcessAsync(delivery, CancellationToken.None);
        if (outcome == "error") await Assert.ThrowsAsync<InvalidOperationException>(operation);
        else if (outcome == "cancelled") await Assert.ThrowsAsync<OperationCanceledException>(operation);
        else await operation();
        Assert.Empty(_journal.Entries);
        Assert.Equal(0, _deliveryRepository.PendingChecks);
        Assert.Equal(CampaignStatus.InProgress, campaign.Status);
    }

    [Fact]
    public async Task Process_OperatorCancellation_DoesNotBecomeFailure()
    {
        var delivery = AddWaitingDelivery(CreateStartedCampaign());
        delivery.SetCapturedForTest("worker-1", DateTime.UtcNow.AddMinutes(2));
        _sender.AsyncHandler = (_, _) => throw new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => _service.ProcessAsync(delivery, CancellationToken.None));
        Assert.Equal(DeliveryStatus.InProgress, delivery.DeliveryStatus);
        Assert.Null(delivery.CompletedAt);
        Assert.Empty(_journal.Entries);
        Assert.Equal(0, _deliveryRepository.PendingChecks);
    }
}
