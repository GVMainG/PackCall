using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
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
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(new[] { "email", "telegram" }, await response.Content.ReadFromJsonAsync<string[]>());
        }
    }

    [PostgresFact]
    public async Task AllIncludesContactsWithoutASelectedAddressAndAllowsZeroDeliveries()
    {
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        await using var scope = _provider!.CreateAsyncScope();
        var contacts = scope.ServiceProvider.GetRequiredService<IContactRepository>();
        var contact = await contacts.CreateAsync(Contact.Create([("telegram", "42")], DateTime.UtcNow), default);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var fields in new[] { "", """, "contactIds":null""" })
        {
            using var request = CampaignRequest(""", "selection":"all", "channels":["email"]""" + fields);
            using var response = await client.PostAsync("/api/campaigns", request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var campaign = await response.Content.ReadFromJsonAsync<JsonElement>();
            var campaignId = campaign.GetProperty("id").GetGuid();
            Assert.Equal("Draft", campaign.GetProperty("status").GetString());
            var selected = await db.CampaignContacts.SingleAsync(row => row.CampaignId == campaignId);
            Assert.Equal(contact.Id, selected.ContactId);
            Assert.False(await db.Deliveries.AnyAsync(d => d.CampaignId == campaignId));
            var details = await client.GetFromJsonAsync<JsonElement>(response.Headers.Location);
            var formation = details.GetProperty("formation");
            Assert.Equal(1, formation.GetProperty("selectedContactCount").GetInt32());
            Assert.Equal(0, formation.GetProperty("createdDeliveryCount").GetInt32());
            Assert.Equal(1, formation.GetProperty("skippedDeliveryCount").GetInt32());
        }
    }

    [PostgresFact]
    public async Task InvalidCreationReturnsProblemDetailsWithoutWritingCampaignRows()
    {
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await campaigns.CreateRecipientAsync("test@example.com", default);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var invalidFields = new[]
        {
            ("", "Укажите режим выборки"),
            (""", "channels":["email"]""", "Укажите режим выборки"),
            (""", "selection":null, "channels":["email"]""", "Укажите режим выборки"),
            (""", "selection":"all" """, "Укажите каналы кампании"),
            (""", "selection":"all", "channels":null """, "Укажите каналы кампании"),
            (""", "selection":"All", "channels":["email"]""", "Допустимые режимы выборки"),
            (""", "selection":"contactIds", "channels":["email"]""", "укажите хотя бы один контакт"),
            (""", "selection":"contactIds", "contactIds":[], "channels":["email"]""", "Укажите хотя бы один контакт"),
            ($$""", "selection":"all", "contactIds":["{{contact.Id}}"], "channels":["email"]""", "поле contactIds"),
            ($$""", "selection":"contactIds", "contactIds":["{{contact.Id}}","{{Guid.NewGuid()}}"], "channels":["email"]""",
                "выбранных контактов не найдены"),
            (""", "selection":"contactIds", "contactIds":["bad-uuid"], "channels":["email"]""", "$.contactIds[0]")
        };
        var invalidChannels = new[]
        {
            ("[]", "Укажите хотя бы один канал"), ("""["sms"]""", "Допустимые каналы"),
            ("""["Email"]""", "Допустимые каналы"), ("""[""]""", "Допустимые каналы"),
            ("[null]", "Допустимые каналы"), ("1", "$.channels")
        };
        foreach (var (fields, reason) in invalidFields.Concat(invalidChannels.Select(
            item => ($""", "selection":"all", "channels":{item.Item1}""", item.Item2))))
        {
            using var request = CampaignRequest(fields);
            using var response = await client.PostAsync("/api/campaigns", request);
            await AssertBadRequest(response, reason, db);
        }
    }

    [PostgresFact]
    public async Task PublicCreationValidatesNameAndTextLimitsBeforeWriting()
    {
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invalid = new (string? Name, string? Text, string Reason)[]
        {
            (null, "text", "Укажите название"), ("", "text", "Укажите название"),
            (" \t", "text", "Укажите название"), (new string('n', 256), "text", "255 символов"),
            ("name", null, "Укажите текст"), ("name", "", "Укажите текст"),
            ("name", " \t", "Укажите текст"), ("name", new string('t', 4097), "4096 символов"),
            ("na\0me", "text", "нулевой символ"), ("name", "te\0xt", "нулевой символ")
        };
        foreach (var (name, messageText, reason) in invalid)
        {
            using var response = await client.PostAsJsonAsync("/api/campaigns",
                new { name, messageText, selection = "all", channels = new[] { "telegram" } });
            await AssertBadRequest(response, reason, db);
        }
        using var mixed = await client.PostAsJsonAsync("/api/campaigns", new
        {
            name = "Mixed channels", messageText = new string('t', 4097), selection = "all",
            channels = new[] { "email", "telegram" }
        });
        await AssertBadRequest(mixed, "4096 символов", db);

        var boundaryName = new string('n', 254) + "😀";
        foreach (var (channel, length) in new[] { ("telegram", 4096), ("email", 4097) })
        {
            var messageText = new string('t', length - 1) + "😀";
            using var response = await client.PostAsJsonAsync("/api/campaigns",
                new { name = boundaryName, messageText, selection = "all", channels = new[] { channel } });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(boundaryName, body.GetProperty("name").GetString());
            Assert.Equal(messageText, body.GetProperty("messageText").GetString());
            Assert.Equal("Draft", body.GetProperty("status").GetString());
        }
        Assert.Equal(2, await db.Campaigns.CountAsync());
        Assert.False(await db.CampaignContacts.AnyAsync());
        Assert.False(await db.Deliveries.AnyAsync());
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
        using var created = await client.PostAsJsonAsync("/api/campaigns", new
        {
            name = "Multiple channels", messageText = "text", selection = "all",
            channels = new[] { "email", "telegram" }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var campaignId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.True(await db.CampaignChannels.AnyAsync(c => c.CampaignId == campaignId && c.Channel == "telegram"));

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

    private static async Task AssertBadRequest(HttpResponseMessage response, string reason, AppDbContext db)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        var reasons = string.Join(" ", problem.GetProperty("errors").EnumerateObject()
            .SelectMany(field => field.Value.EnumerateArray()).Select(message => message.GetString()));
        Assert.Contains(reason, reasons);
        Assert.False(await db.Campaigns.AnyAsync());
        Assert.False(await db.CampaignContacts.AnyAsync());
        Assert.False(await db.CampaignChannels.AnyAsync());
        Assert.False(await db.Deliveries.AnyAsync());
    }
}
