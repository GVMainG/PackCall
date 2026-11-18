using Microsoft.AspNetCore.Mvc;
using PackCall.Api.Controllers;
using PackCall.Api.Models;
using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Sending;
using PackCall.Core.Services;

namespace PackCall.Core.Test;

[Trait("Category", "Unit")]
[Trait("Module", "Campaigns")]
public class CampaignServiceTests
{
    private readonly FakeCampaignRepository _campaignRepository;
    private readonly FakeDeliveryRepository _deliveryRepository = new();
    private readonly FakeContactRepository _recipientRepository = new();
    private readonly ICampaignService _service;

    public CampaignServiceTests()
    {
        _campaignRepository = new FakeCampaignRepository(_deliveryRepository);
        _service = new CampaignService(_campaignRepository, _deliveryRepository, _recipientRepository,
            new EmailOperatorAvailability());
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
        _recipientRepository.Items.Add(Contact.Create([(ContactChannel.Email, "User@Example.com")], DateTime.UtcNow));

        await Assert.ThrowsAsync<RecipientAlreadyExistsException>(() =>
            _service.CreateRecipientAsync(" user@example.com ", CancellationToken.None));
        Assert.Single(_recipientRepository.Items);
    }

    [Fact]
    public async Task CreateCampaign_PersistsDraftAndOneWaitingDeliveryPerRecipient()
    {
        var recipient = Contact.Create([(ContactChannel.Email, "first@example.com")], DateTime.UtcNow);
        _recipientRepository.Items.Add(recipient);
        _recipientRepository.Items.Add(Contact.Create([(ContactChannel.Email, "second@example.com")], DateTime.UtcNow));

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
        Assert.Equal(2, _deliveryRepository.Items.Select(delivery => delivery.ContactId).Distinct().Count());

        recipient.ReplaceAddresses([(ContactChannel.Email, "changed@example.com")], DateTime.UtcNow);
        Assert.Equal("first@example.com", _deliveryRepository.Items.Single(d => d.ContactId == recipient.Id).AddressSnapshot);
    }

    [Fact]
    public async Task CreateCampaign_WithInvalidRecipient_PersistsNothing()
    {
        _recipientRepository.Items.Add(Contact.Create([(ContactChannel.Email, "valid@example.com")], DateTime.UtcNow));
        var invalid = Contact.Create([(ContactChannel.Email, "legacy@example.com")], DateTime.UtcNow);
        // Эмулируем исторический адрес, сохранённый до доменной валидации.
        typeof(ContactChannel).GetProperty(nameof(ContactChannel.Address))!.SetValue(invalid.Channels.Single(), " ");
        _recipientRepository.Items.Add(invalid);

        var error = await Assert.ThrowsAsync<CampaignFormationException>(() => CreateCampaignAsync());
        Assert.IsType<ArgumentException>(error.InnerException);

        Assert.Empty((await _service.GetCampaignsAsync(1, 10, CancellationToken.None)).Items);
        Assert.Empty(_deliveryRepository.Items);
    }

    [Fact]
    public async Task CreateCampaign_FormsDeliveriesInBatchesAndBlocksChangesUntilReady()
    {
        _recipientRepository.Items.AddRange(Enumerable.Range(0, 1001)
            .Select(index => Contact.Create([(ContactChannel.Email, $"user{index}@example.com")], DateTime.UtcNow)));
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
    public async Task CreateCampaign_WhenInitialWriteLosesAcknowledgment_RemovesStoredCampaign()
    {
        var writeError = new OperationCanceledException("Внутренняя отмена записи.");
        _campaignRepository.AfterCreate = () => throw writeError;

        var error = await Assert.ThrowsAsync<CampaignFormationException>(CreateCampaignAsync);

        Assert.Same(writeError, error.InnerException);
        Assert.Empty((await _service.GetCampaignsAsync(1, 10, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task CreateCampaign_WhenLaterBatchFails_RemovesPartialCampaign()
    {
        _recipientRepository.Items.AddRange(Enumerable.Range(0, 501)
            .Select(index => Contact.Create([(ContactChannel.Email, $"user{index}@example.com")], DateTime.UtcNow)));
        var batchNumber = 0;
        _deliveryRepository.BeforeAddBatchAsync = _ =>
        {
            if (++batchNumber == 2)
                throw new InvalidOperationException("Ошибка сохранения порции");
            return Task.CompletedTask;
        };

        var error = await Assert.ThrowsAsync<CampaignFormationException>(() => CreateCampaignAsync());
        Assert.IsType<InvalidOperationException>(error.InnerException);

        Assert.Empty((await _service.GetCampaignsAsync(1, 10, CancellationToken.None)).Items);
        Assert.Empty(_deliveryRepository.Items);
    }

    [Fact]
    public async Task CreateCampaign_WhenCancelledAfterFirstBatch_RemovesPartialCampaign()
    {
        _recipientRepository.Items.AddRange(Enumerable.Range(0, 501)
            .Select(index => Contact.Create([(ContactChannel.Email, $"user{index}@example.com")], DateTime.UtcNow)));
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
        _recipientRepository.Items.Add(Contact.Create([(ContactChannel.Email, "user@example.com")], DateTime.UtcNow));
        var id = await CreateCampaignAsync();
        await _service.StartCampaignAsync(id, CancellationToken.None);
        var campaign = (await _service.GetCampaignAsync(id, CancellationToken.None))!;
        var startedAt = campaign.StartedAt;
        var updatedAt = campaign.UpdatedAt;
        var delivery = _deliveryRepository.Items.Single();
        delivery.SetCapturedForTest("worker-1", DateTime.UtcNow.AddMinutes(2));

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

        await Assert.ThrowsAsync<InvalidStatusTransitionException>(() =>
            _service.StartCampaignAsync(id, CancellationToken.None));

        Assert.Equal(CampaignStatus.Completed, campaign.Status);
    }

    [Fact]
    public async Task DeleteCampaign_InProgress_Throws()
    {
        _recipientRepository.Items.Add(Contact.Create([(ContactChannel.Email, "user@example.com")], DateTime.UtcNow));
        var id = await CreateCampaignAsync();
        await _service.StartCampaignAsync(id, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.DeleteCampaignAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task StartCampaign_EmptySnapshotCompletesWithNoOperatorsAvailable()
    {
        var service = new CampaignService(_campaignRepository, _deliveryRepository, _recipientRepository,
            new NoOperatorAvailability());
        var id = await CreateCampaignAsync();

        var result = await service.StartCampaignAsync(id, default);

        Assert.Equal(CampaignStatus.Completed, result.Status);
        Assert.NotNull(result.StartedAt);
        Assert.NotNull(result.CompletedAt);
        Assert.Empty(_deliveryRepository.Items);
    }

    [Fact]
    public async Task StartResponseUsesSavedCompletionWhenCampaignIsDeletedBeforeReread()
    {
        var id = await CreateCampaignAsync();
        var reads = 0;
        _campaignRepository.OnGet = () =>
        {
            if (++reads == 2)
                _ = _campaignRepository.DeleteIfInactiveAsync(id, default);
        };

        var response = await new CampaignsController(_service).StartCampaign(id, default);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<CampaignResponse>(ok.Value);
        Assert.Equal(id, body.Id);
        Assert.Equal(CampaignStatus.Completed, body.Status);
        Assert.NotNull(body.StartedAt);
        Assert.NotNull(body.CompletedAt);
        Assert.Null(await _service.GetCampaignAsync(id, default));
    }

    [Fact]
    public async Task ResumeFinishesAfterCancellationAtSuccessfulStatusWrite()
    {
        _recipientRepository.Items.Add(Contact.Create([(ContactChannel.Email, "user@example.com")], DateTime.UtcNow));
        var id = await CreateCampaignAsync();
        var started = await _service.StartCampaignAsync(id, default);
        var startedAt = started.StartedAt;
        var delivery = Assert.Single(_deliveryRepository.Items);
        delivery.SetCapturedForTest("worker", DateTime.UtcNow.AddMinutes(1));
        await _service.PauseCampaignAsync(id, default);

        using var request = new CancellationTokenSource();
        _campaignRepository.OnTryUpdateStatus = () =>
        {
            _campaignRepository.OnTryUpdateStatus = null;
            delivery.StatusFromInProgressToSent(DateTime.UtcNow);
            request.Cancel();
        };

        var resumed = await _service.ResumeCampaignAsync(id, request.Token);

        Assert.True(request.IsCancellationRequested);
        Assert.Equal(CampaignStatus.Completed, resumed.Status);
        Assert.Equal(startedAt, resumed.StartedAt);
        Assert.NotNull(resumed.CompletedAt);
        Assert.Equal(CampaignStatus.Completed, (await _service.GetCampaignAsync(id, default))!.Status);
    }

    private sealed class NoOperatorAvailability : IChannelOperatorAvailability
    {
        public bool HasOperator(string channel) => false;
    }

}
