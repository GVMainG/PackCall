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

    [Theory]
    [InlineData("")]
    [InlineData("invalid-email")]
    [InlineData("Name <user@example.com>")]
    public async Task CreateRecipient_RejectsInvalidEmail(string email)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateRecipientAsync(email, CancellationToken.None));
        Assert.Empty(_recipientRepository.Items);
    }

    [Fact]
    public async Task CreateRecipient_RejectsDuplicateIgnoringCase()
    {
        _recipientRepository.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = "User@Example.com" });

        await Assert.ThrowsAsync<RecipientAlreadyExistsException>(() =>
            _service.CreateRecipientAsync(" user@example.com ", CancellationToken.None));
        Assert.Single(_recipientRepository.Items);
    }

    [Fact]
    public async Task CreateCampaign_PersistsDraftAndOneWaitingDeliveryPerRecipient()
    {
        var recipient = new Recipient { Id = Guid.NewGuid(), Email = "first@example.com" };
        _recipientRepository.Items.Add(recipient);
        _recipientRepository.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = "second@example.com" });

        var id = await CreateCampaignAsync();
        var campaign = await _service.GetCampaignAsync(id, CancellationToken.None);

        Assert.NotNull(campaign);
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.Equal(2, _deliveryRepository.Items.Count);
        Assert.All(_deliveryRepository.Items, delivery =>
        {
            Assert.Equal(id, delivery.CampaignId);
            Assert.Equal(DeliveryStatus.Waiting, delivery.DeliveryStatus);
        });
        Assert.Equal(2, _deliveryRepository.Items.Select(delivery => delivery.RecipientId).Distinct().Count());

        recipient.Email = "changed@example.com";
        Assert.Equal("first@example.com", _deliveryRepository.Items.Single(d => d.RecipientId == recipient.Id).RecipientEmail);
    }

    [Fact]
    public async Task CreateCampaign_WithInvalidRecipient_PersistsNothing()
    {
        _recipientRepository.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = "valid@example.com" });
        _recipientRepository.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = " " });

        await Assert.ThrowsAsync<ArgumentException>(() => CreateCampaignAsync());

        Assert.Empty((await _service.GetCampaignsAsync(1, 10, CancellationToken.None)).Items);
        Assert.Empty(_deliveryRepository.Items);
    }

    [Fact]
    public async Task CreateCampaign_FormsDeliveriesInBatchesAndBlocksChangesUntilReady()
    {
        _recipientRepository.Items.AddRange(Enumerable.Range(0, 1001)
            .Select(index => new Recipient { Id = Guid.NewGuid(), Email = $"user{index}@example.com" }));
        var batchSizes = new List<int>();
        _deliveryRepository.BeforeAddBatchAsync = async deliveries =>
        {
            batchSizes.Add(deliveries.Count);
            var campaignId = deliveries[0].CampaignId;
            Assert.Equal(CampaignStatus.Forming,
                (await _service.GetCampaignAsync(campaignId, CancellationToken.None))!.Status);
            await Assert.ThrowsAsync<InvalidStatusTransitionException>(() =>
                _service.StartCampaignAsync(campaignId, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _service.UpdateCampaignAsync(campaignId, "Новое имя", "Новый текст", CancellationToken.None));
        };

        var id = await CreateCampaignAsync();

        Assert.True(batchSizes.Count > 1);
        Assert.Equal(1001, batchSizes.Sum());
        Assert.Equal(1001, _deliveryRepository.Items.Count);
        Assert.Equal(CampaignStatus.Draft, (await _service.GetCampaignAsync(id, CancellationToken.None))!.Status);
        await _service.StartCampaignAsync(id, CancellationToken.None);
        Assert.Equal(CampaignStatus.InProgress, (await _service.GetCampaignAsync(id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task CreateCampaign_WhenLaterBatchFails_RemovesPartialCampaign()
    {
        _recipientRepository.Items.AddRange(Enumerable.Range(0, 501)
            .Select(index => new Recipient { Id = Guid.NewGuid(), Email = $"user{index}@example.com" }));
        var batchNumber = 0;
        _deliveryRepository.BeforeAddBatchAsync = _ =>
        {
            if (++batchNumber == 2)
                throw new InvalidOperationException("Ошибка сохранения порции");
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCampaignAsync());

        Assert.Empty((await _service.GetCampaignsAsync(1, 10, CancellationToken.None)).Items);
        Assert.Empty(_deliveryRepository.Items);
    }

    [Fact]
    public async Task CreateCampaign_WhenCancelledAfterFirstBatch_RemovesPartialCampaign()
    {
        _recipientRepository.Items.AddRange(Enumerable.Range(0, 501)
            .Select(index => new Recipient { Id = Guid.NewGuid(), Email = $"user{index}@example.com" }));
        using var cancellation = new CancellationTokenSource();
        _deliveryRepository.BeforeAddBatchAsync = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.CreateCampaignAsync("Кампания", "Текст", cancellation.Token));

        Assert.Empty((await _service.GetCampaignsAsync(1, 10, CancellationToken.None)).Items);
        Assert.Empty(_deliveryRepository.Items);
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

}
