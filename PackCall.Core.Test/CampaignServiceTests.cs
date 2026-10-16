using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Services;

namespace PackCall.Core.Test;

public class CampaignServiceTests
{
    private readonly FakeCampaignRepository _campaignRepository;
    private readonly FakeDeliveryRepository _deliveryRepository = new();
    private readonly FakeRecipientRepository _recipientRepository = new();
    private readonly ICampaignService _service;

    public CampaignServiceTests()
    {
        _campaignRepository = new FakeCampaignRepository(_deliveryRepository);
        _service = new CampaignService(_campaignRepository, _deliveryRepository, _recipientRepository);
    }

    private Task<Guid> CreateCampaignAsync() =>
        _service.CreateCampaignAsync("Кампания", "Текст", CancellationToken.None);

    [Fact]
    public async Task CreateCampaign_PersistsDraftAndOneWaitingDeliveryPerRecipient()
    {
        var recipient = new Recipient { Id = Guid.NewGuid(), Email = "first@example.com" };
        _recipientRepository.Items.Add(recipient);
        _recipientRepository.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = "second@example.com" });
        _recipientRepository.Items.Add(recipient);

        var id = await CreateCampaignAsync();
        var campaign = await _service.GetCampaignAsync(id, CancellationToken.None);

        Assert.NotNull(campaign);
        Assert.Equal("Кампания", campaign.Name);
        Assert.Equal("Текст", campaign.MessageText);
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.NotNull(campaign.CreatedAt);
        Assert.Equal(campaign.CreatedAt, campaign.UpdatedAt);
        Assert.Null(campaign.StartedAt);
        Assert.Null(campaign.CompletedAt);
        Assert.Equal(2, _deliveryRepository.Items.Count);
        Assert.Equal(2, campaign.Deliveries.Count);
        Assert.All(_deliveryRepository.Items, delivery =>
        {
            Assert.Equal(id, delivery.CampaignId);
            Assert.Same(campaign, delivery.Campaign);
            Assert.Equal(DeliveryStatus.Waiting, delivery.DeliveryStatus);
            Assert.Equal(delivery.CreatedAt, delivery.UpdatedAt);
            Assert.Equal(delivery.Recipient.Email, delivery.RecipientEmail);
            Assert.Null(delivery.CompletedAt);
            Assert.Null(delivery.WorkerId);
            Assert.Null(delivery.LockExpiresAt);
        });

        recipient.Email = "changed@example.com";
        Assert.Equal("first@example.com", _deliveryRepository.Items.Single(d => d.RecipientId == recipient.Id).RecipientEmail);
        var recipients = await _service.GetRecipientsAsync(id, 1, 10, CancellationToken.None);
        Assert.Equal(2, recipients.Items.Count);
    }

    [Fact]
    public async Task CreateCampaign_WithInvalidRecipient_PersistsNothing()
    {
        _recipientRepository.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = "valid@example.com" });
        _recipientRepository.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = " " });

        await Assert.ThrowsAsync<ArgumentException>(() => CreateCampaignAsync());

        Assert.Equal(0, _campaignRepository.CreateCount);
        Assert.Empty(_deliveryRepository.Items);
    }

    [Fact]
    public async Task CreateCampaign_WhenCancelled_PersistsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.CreateCampaignAsync("Кампания", "Текст", cancellation.Token));

        Assert.Equal(0, _campaignRepository.CreateCount);
        Assert.Empty(_deliveryRepository.Items);
    }

    [Fact]
    public async Task StartCampaign_TransitionsToInProgress()
    {
        var id = await CreateCampaignAsync();

        await _service.StartCampaignAsync(id, CancellationToken.None);

        var campaign = await _service.GetCampaignAsync(id, CancellationToken.None);
        Assert.Equal(CampaignStatus.InProgress, campaign!.Status);
        Assert.NotNull(campaign.StartedAt);
        Assert.Equal(campaign.StartedAt, campaign.UpdatedAt);
    }

    [Fact]
    public async Task StartCampaign_WhenAlreadyStarted_DoesNotChangeDatesOrDeliveries()
    {
        _recipientRepository.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = "user@example.com" });
        var id = await CreateCampaignAsync();
        await _service.StartCampaignAsync(id, CancellationToken.None);
        var campaign = (await _service.GetCampaignAsync(id, CancellationToken.None))!;
        var startedAt = campaign.StartedAt;
        var updatedAt = campaign.UpdatedAt;
        var delivery = _deliveryRepository.Items.Single();
        delivery.StatusFromWaitingToInProgress("worker-1", DateTime.UtcNow.AddMinutes(2));

        await _service.StartCampaignAsync(id, CancellationToken.None);

        Assert.Equal(startedAt, campaign.StartedAt);
        Assert.Equal(updatedAt, campaign.UpdatedAt);
        Assert.Equal(1, _campaignRepository.UpdateCount);
        Assert.Same(delivery, Assert.Single(_deliveryRepository.Items));
        Assert.Equal(DeliveryStatus.InProgress, delivery.DeliveryStatus);
    }

    [Fact]
    public async Task StartCampaign_WhenCompleted_RejectsRestart()
    {
        var id = await CreateCampaignAsync();
        await _service.StartCampaignAsync(id, CancellationToken.None);
        var campaign = (await _service.GetCampaignAsync(id, CancellationToken.None))!;
        campaign.StatusFromInProgressToCompleted(DateTime.UtcNow);

        await Assert.ThrowsAsync<InvalidStatusTransitionException>(() =>
            _service.StartCampaignAsync(id, CancellationToken.None));

        Assert.Equal(CampaignStatus.Completed, campaign.Status);
    }

    [Fact]
    public async Task DeleteCampaign_InProgress_Throws()
    {
        var id = await CreateCampaignAsync();
        await _service.StartCampaignAsync(id, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.DeleteCampaignAsync(id, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public async Task PageQueries_WithInvalidPage_RejectArguments(int pageNumber, int pageSize)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _service.GetCampaignsAsync(pageNumber, pageSize, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _service.GetDeliveriesAsync(Guid.NewGuid(), pageNumber, pageSize, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _service.GetRecipientsAsync(Guid.NewGuid(), pageNumber, pageSize, CancellationToken.None));
    }

    [Fact]
    public async Task MissingCampaign_QueriesReturnEmptyAndCommandsReportNotFound()
    {
        var id = Guid.NewGuid();

        Assert.Null(await _service.GetCampaignAsync(id, CancellationToken.None));
        Assert.False(await _service.DeleteCampaignAsync(id, CancellationToken.None));
        var deliveries = await _service.GetDeliveriesAsync(id, 1, 10, CancellationToken.None);
        Assert.Empty(deliveries.Items);
        Assert.Equal(0, deliveries.TotalCount);
        var recipients = await _service.GetRecipientsAsync(id, 1, 10, CancellationToken.None);
        Assert.Empty(recipients.Items);
        Assert.Equal(0, recipients.TotalCount);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _service.StartCampaignAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _service.UpdateCampaignAsync(id, "Название", "Текст", CancellationToken.None));
    }

    [Fact]
    public async Task ReadOperations_ReturnDeliveryRecipientAndStatusCounts()
    {
        var recipient = new Recipient { Id = Guid.NewGuid(), Email = "user@example.com" };
        _recipientRepository.Items.Add(recipient);
        var campaignId = await CreateCampaignAsync();
        var delivery = Assert.Single(_deliveryRepository.Items);

        Assert.Same(delivery, await _service.GetDeliveryAsync(delivery.Id, CancellationToken.None));
        Assert.Same(recipient, await _service.GetRecipientAsync(recipient.Id, CancellationToken.None));
        Assert.Null(await _service.GetDeliveryAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await _service.GetRecipientAsync(Guid.NewGuid(), CancellationToken.None));

        var recipients = await _service.GetRecipientsAsync(1, 10, CancellationToken.None);
        Assert.Same(recipient, Assert.Single(recipients.Items));
        Assert.Equal(1, recipients.TotalCount);
        Assert.Equal(new DeliveryStatusCounts(1, 1, 0, 0, 0),
            await _service.GetDeliveryStatusCountsAsync(campaignId, CancellationToken.None));
    }
}
