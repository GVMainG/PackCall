using Microsoft.AspNetCore.Mvc;
using PackCall.Api.Controllers;
using PackCall.Api.Models;
using PackCall.Core.Entities;
using PackCall.Core.Services;

namespace PackCall.Core.Test;

public class ApiControllerTests
{
    private readonly FakeDeliveryRepository _deliveries = new();
    private readonly FakeRecipientRepository _recipients = new();
    private readonly ICampaignService _service;

    public ApiControllerTests()
    {
        _service = new CampaignService(new FakeCampaignRepository(_deliveries), _deliveries, _recipients);
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
        Assert.Equal(recipient.Id, delivery.RecipientId);
        Assert.Equal(recipient.Email, delivery.RecipientEmail);
    }

    [Fact]
    public async Task CampaignEndpoints_CreateReadAndPatchPreserveOtherField()
    {
        _recipients.Items.Add(new Recipient { Id = Guid.NewGuid(), Email = "user@example.com" });
        var controller = new CampaignsController(_service);

        var created = await controller.CreateCampaign(
            new CreateCampaignRequest { Name = "Название", MessageText = "Текст" }, CancellationToken.None);
        var result = Assert.IsType<CreatedAtActionResult>(created.Result);
        var campaign = Assert.IsType<CampaignResponse>(result.Value);
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
            .Select(index => new Recipient { Id = Guid.NewGuid(), Email = $"user{index}@example.com" }));
        var page = await recipients.GetRecipients(2, 2, CancellationToken.None);
        var response = Assert.IsType<PageResponse<RecipientResponse>>(
            Assert.IsType<OkObjectResult>(page.Result).Value);
        Assert.Single(response.Items);
        Assert.Equal(3, response.TotalCount);
        Assert.Equal(2, response.TotalPages);
    }
}
