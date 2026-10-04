using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Services;
using PackCall.Core.Sending;

namespace PackCall.Core.Test;

public class CampaignWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateStartAndProcess_CompletesOnlyAfterEveryDeliveryIsTerminal(bool failLastDelivery)
    {
        var deliveries = new FakeDeliveryRepository();
        var campaigns = new FakeCampaignRepository(deliveries);
        var recipients = new FakeRecipientRepository();
        recipients.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = "first@example.com" });
        recipients.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = "second@example.com" });
        var campaignService = new CampaignService(campaigns, deliveries, recipients);
        var sender = new FakeMessageSender();
        var journal = new FakeJournal();
        var metrics = new FakeMetrics();
        var processing = new DeliveryProcessingService(deliveries, campaigns, sender, journal, metrics);

        var id = await campaignService.CreateCampaignAsync("Рассылка", "Сообщение", CancellationToken.None);
        var campaign = (await campaignService.GetCampaignAsync(id, CancellationToken.None))!;
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.Equal(2, deliveries.Items.Count);
        Assert.Empty(await processing.CaptureBatchAsync(2, "worker-1", TimeSpan.FromMinutes(2), CancellationToken.None));

        await campaignService.StartCampaignAsync(id, CancellationToken.None);
        var batch = await processing.CaptureBatchAsync(2, "worker-1", TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Equal(2, batch.Count);

        await processing.ProcessAsync(batch[0], CancellationToken.None);
        Assert.Equal(DeliveryStatus.Sent, batch[0].DeliveryStatus);
        Assert.Equal(CampaignStatus.InProgress, campaign.Status);
        Assert.Null(campaign.CompletedAt);

        DateTime operatorReturnedAt = default;
        sender.Handler = _ =>
        {
            operatorReturnedAt = DateTime.UtcNow;
            return failLastDelivery ? SendResult.Failure("Ошибка оператора") : SendResult.Success();
        };
        await processing.ProcessAsync(batch[1], CancellationToken.None);

        Assert.Equal(failLastDelivery ? DeliveryStatus.Failed : DeliveryStatus.Sent, batch[1].DeliveryStatus);
        Assert.True(batch[1].CompletedAt >= operatorReturnedAt);
        Assert.Equal(CampaignStatus.Completed, campaign.Status);
        Assert.NotNull(campaign.CompletedAt);
        Assert.Equal(campaign.CompletedAt, campaign.UpdatedAt);
        Assert.All(batch, delivery =>
        {
            Assert.True(DeliveryStatusTransitions.IsTerminal(delivery.DeliveryStatus));
            Assert.Null(delivery.WorkerId);
            Assert.Null(delivery.LockExpiresAt);
        });
        Assert.Equal(2, sender.Requests.Count);
        Assert.All(sender.Requests, request =>
        {
            Assert.Equal(id, request.CampaignId);
            Assert.Equal("Сообщение", request.MessageText);
            Assert.Contains(batch, delivery => delivery.Id == request.DeliveryId &&
                delivery.RecipientId == request.RecipientId && delivery.RecipientEmail == request.RecipientEmail);
        });
        Assert.Equal(4, journal.Entries.Count);
        Assert.All(journal.Entries, entry => Assert.Equal("worker-1", entry.WorkerId));
        Assert.Equal(2, metrics.Processed);
        Assert.Equal(failLastDelivery ? 1 : 2, metrics.OperatorSuccess);
        Assert.Equal(failLastDelivery ? 1 : 0, metrics.OperatorErrors);

        var completedAt = campaign.CompletedAt;
        var updates = campaigns.UpdateCount;
        Assert.False(await processing.CompleteCampaignIfFinishedAsync(id, CancellationToken.None));
        Assert.Equal(completedAt, campaign.CompletedAt);
        Assert.Equal(updates, campaigns.UpdateCount);
        await Assert.ThrowsAsync<DomainException>(() => processing.ProcessAsync(batch[1], CancellationToken.None));
        Assert.Equal(2, sender.Requests.Count);
    }
}
