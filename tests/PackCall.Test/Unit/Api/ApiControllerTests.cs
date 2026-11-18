using Microsoft.AspNetCore.Mvc;
using PackCall.Api.Controllers;
using PackCall.Api.Models;
using PackCall.Core.Entities;
using PackCall.Core.Services;

namespace PackCall.Core.Test;

[Trait("Category", "Unit")]
[Trait("Module", "Api")]
public class ApiControllerTests
{
    private readonly FakeDeliveryRepository _deliveries = new();
    private readonly FakeContactRepository _recipients = new();
    private readonly FakeCampaignRepository _campaigns;
    private readonly ICampaignService _service;

    public ApiControllerTests()
    {
        _campaigns = new FakeCampaignRepository(_deliveries);
        _service = new CampaignService(_campaigns, _deliveries, _recipients,
            new EmailOperatorAvailability());
    }

    [Fact]
    public async Task CreateRecipient_ReturnsCreatedResourceAndIncludesItInNewCampaign()
    {
        var recipients = new RecipientsController(_service);
        var created = await recipients.CreateRecipient(
            new CreateRecipientRequest { Email = " User@Example.com " }, CancellationToken.None);

        var result = Assert.IsType<CreatedAtActionResult>(created.Result);
        var recipient = Assert.IsType<RecipientResponse>(result.Value);
        Assert.Equal(nameof(RecipientsController.GetRecipient), result.ActionName);
        Assert.Equal(recipient.Id, result.RouteValues!["id"]);
        Assert.Equal("user@example.com", recipient.Email);

        var campaignId = await _service.CreateCampaignAsync("Кампания", "Текст", CancellationToken.None);
        var delivery = Assert.Single(_deliveries.Items);
        Assert.Equal(campaignId, delivery.CampaignId);
        Assert.Equal(recipient.Id, delivery.ContactId);
        Assert.Equal(recipient.Email, delivery.AddressSnapshot);
    }

    [Fact]
    public async Task CampaignEndpoints_CreateReadAndPatchPreserveOtherField()
    {
        var controller = new CampaignsController(_service);
        var contact = Contact.Create([(ContactChannel.Email, "user@example.com")], DateTime.UtcNow);
        _campaigns.OnCreateSnapshot = (campaign, contactIds, channels) =>
        {
            Assert.NotNull(contactIds);
            Assert.Equal(contact.Id, Assert.Single(contactIds));
            Assert.Equal(ContactChannel.Email, Assert.Single(channels));
            Assert.Equal(CampaignStatus.Forming, campaign.Status);
        };
        _deliveries.OnFormSnapshotDeliveries = id =>
        {
            _deliveries.Add(Delivery.Create(id, contact.Id, "user@example.com", DateTime.UtcNow));
            return 1;
        };

        _campaigns.OnGet = () => throw new InvalidOperationException("POST не должен повторно читать кампанию.");
        var created = await controller.CreateCampaign(
            new CreateCampaignRequest
            {
                Name = "Название", MessageText = "Текст", Selection = "contactIds",
                ContactIds = [contact.Id], Channels = [ContactChannel.Email]
            }, CancellationToken.None);
        _campaigns.OnGet = null;
        var result = Assert.IsType<CreatedAtActionResult>(created.Result);
        var campaign = Assert.IsType<CampaignResponse>(result.Value);
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.Equal(nameof(CampaignsController.GetCampaign), result.ActionName);
        Assert.Equal(campaign.Id, result.RouteValues!["id"]);

        var detail = await controller.GetCampaign(campaign.Id, CancellationToken.None);
        var response = Assert.IsType<CampaignDetailsResponse>(Assert.IsType<OkObjectResult>(detail.Result).Value);
        Assert.Equal(1, response.Deliveries.Total);
        Assert.Equal(1, response.Deliveries.Waiting);

        var patched = await controller.UpdateCampaign(campaign.Id,
            new UpdateCampaignRequest { Name = "Новое название" }, CancellationToken.None);
        var updated = Assert.IsType<CampaignResponse>(Assert.IsType<OkObjectResult>(patched.Result).Value);
        Assert.Equal("Новое название", updated.Name);
        Assert.Equal("Текст", updated.MessageText);
    }

    [Fact]
    public async Task ReadEndpoints_ReportMissingResourcesAndPageTotals()
    {
        var campaigns = new CampaignsController(_service);
        var deliveries = new DeliveriesController(_service);
        var recipients = new RecipientsController(_service);

        Assert.IsType<NotFoundResult>((await campaigns.GetCampaign(Guid.NewGuid(), CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await deliveries.GetDelivery(Guid.NewGuid(), CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await recipients.GetRecipient(Guid.NewGuid(), CancellationToken.None)).Result);

        _recipients.Items.AddRange(Enumerable.Range(0, 3)
            .Select(index => Contact.Create([(ContactChannel.Email, $"user{index}@example.com")], DateTime.UtcNow)));
        var page = await recipients.GetRecipients(2, 2, CancellationToken.None);
        var response = Assert.IsType<PageResponse<RecipientResponse>>(
            Assert.IsType<OkObjectResult>(page.Result).Value);
        Assert.Single(response.Items);
        Assert.Equal(3, response.TotalCount);
        Assert.Equal(2, response.TotalPages);
    }
}
