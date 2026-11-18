using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Entities;
using PackCall.Core.Services;
using PackCall.Infrastructure;
using PackCall.Infrastructure.Hosting;

namespace PackCall.Core.Test;

[Trait("Category", "Integration")]
[Trait("Module", "Channels")]
public sealed class ChannelsApiPostgresTests : PostgresTestBase
{
    [PostgresFact]
    public async Task ListsSupportedChannelsInBothSenderModes()
    {
        foreach (var mode in new[] { TestSenderMode.Inprocess, TestSenderMode.Http })
        {
            await using var app = await StartApiAsync(mode);
            using var client = CreateClient(app);
            using var response = await client.GetAsync("/api/channels");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new[] { "email", "telegram" }, await response.Content.ReadFromJsonAsync<string[]>());
        }
    }

    [PostgresFact]
    public async Task OmittedNullAndExplicitEmailCreateEmailDeliveries()
    {
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        await campaigns.CreateRecipientAsync("test@example.com", default);

        foreach (var fields in new[] { "", """, "channels":null""", """, "channels":["email"]""" })
        {
            using var request = CampaignRequest(fields);
            using var response = await client.PostAsync("/api/campaigns", request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var campaign = await response.Content.ReadFromJsonAsync<JsonElement>();
            var campaignId = campaign.GetProperty("id").GetGuid();
            Assert.Equal("Draft", campaign.GetProperty("status").GetString());
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var delivery = await db.Deliveries.AsNoTracking().SingleAsync(d => d.CampaignId == campaignId);
            Assert.Equal("email", delivery.Channel);
            Assert.Equal("test@example.com", delivery.AddressSnapshot);
        }
    }

    [PostgresFact]
    public async Task InvalidChannelSelectionReturns400BeforeAnyWrite()
    {
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        await campaigns.CreateRecipientAsync("test@example.com", default);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var channels in new[] { "[]", """["sms"]""", """["Email"]""", """[""]""", """[null]""",
                     """["telegram"]""", """["email","telegram"]""" })
        {
            using var request = CampaignRequest($""", "channels":{channels}""");
            using var response = await client.PostAsync("/api/campaigns", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            var error = Assert.Single(problem.GetProperty("errors").GetProperty("channels").EnumerateArray()).GetString();
            Assert.False(string.IsNullOrWhiteSpace(error));
            if (channels.Contains("telegram", StringComparison.Ordinal))
                Assert.Contains("снимки", error);
            Assert.False(await db.Campaigns.AnyAsync());
            Assert.False(await db.Deliveries.AnyAsync());
        }
    }

    [PostgresFact]
    public async Task StartRejectsMissingOperatorBeforeWorkerCanCapture()
    {
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var legacyId = await campaigns.CreateCampaignAsync("Empty email draft", "text", default);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.CampaignChannels.AnyAsync(c => c.CampaignId == legacyId));
        using var legacy = await client.PostAsync($"/api/campaigns/{legacyId}/start", null);
        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(CampaignStatus.InProgress, (await campaigns.GetCampaignAsync(legacyId, default))!.Status);

        await campaigns.CreateRecipientAsync("test@example.com", default);
        var campaignId = await campaigns.CreateCampaignAsync("Multiple channels", "text", default);
        db.CampaignChannels.Add(new CampaignChannel { CampaignId = campaignId, Channel = "telegram" });
        await db.SaveChangesAsync();

        using var rejected = await client.PostAsync($"/api/campaigns/{campaignId}/start", null);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("telegram", problem.GetProperty("detail").GetString());
        var stored = (await campaigns.GetCampaignAsync(campaignId, default))!;
        Assert.Equal(CampaignStatus.Draft, stored.Status);
        Assert.Null(stored.StartedAt);

        var processing = scope.ServiceProvider.GetRequiredService<IDeliveryProcessingService>();
        Assert.Empty(await processing.CaptureBatchAsync(1, "worker", TimeSpan.FromSeconds(60), default));
        var delivery = await db.Deliveries.AsNoTracking().SingleAsync(d => d.CampaignId == campaignId);
        Assert.Equal(DeliveryStatus.Waiting, delivery.DeliveryStatus);
        Assert.Null(delivery.WorkerId);
    }

    private static StringContent CampaignRequest(string fields) =>
        new($$"""{"name":"Campaign","messageText":"text"{{fields}}}""", Encoding.UTF8, "application/json");
}
