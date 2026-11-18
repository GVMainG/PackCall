using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PackCall.Api.Models;
using PackCall.Core.Entities;
using PackCall.Core.Sending;
using PackCall.Infrastructure;
using PackCall.Worker.Background;
using Serilog;
using Xunit.Abstractions;

namespace PackCall.Core.Test;

[Trait("Category", "Integration")]
[Trait("Module", "Campaigns")]
public sealed class CampaignAudiencePostgresTests(ITestOutputHelper output) : PostgresTestBase
{
    [PostgresFact]
    public async Task WorkerSendsOnlySelectedPairsUsingDraftAddresses()
    {
        await using var app = await StartApiAsync(recordEmailRequests: true);
        using var client = CreateClient(app);
        var both = await CreateContact(new("email", "both@example.com"), new("telegram", "41"));
        var email = await CreateContact(new ContactChannelRequest("email", "email@example.com"));
        var telegram = await CreateContact(new ContactChannelRequest("telegram", "42"));
        await CreateContact(new ContactChannelRequest("email", "excluded@example.com"));
        await CreateContact(new ContactChannelRequest("telegram", "43"));

        Guid[] selected = [both, email, telegram];
        var expectedAddresses = new Dictionary<Guid, string> { [both] = "both@example.com", [email] = "email@example.com" };
        using var created = await client.PostAsJsonAsync("/api/campaigns", new
        {
            name = "SM-2", messageText = "SM-2 message", selection = "contactIds",
            contactIds = selected, channels = new[] { "email" }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var campaign = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", campaign.GetProperty("status").GetString());
        var id = campaign.GetProperty("id").GetGuid();
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await ReadDraft();
        var expectedCalls = original.Select(d => new SendRequest(d.Id, id, d.ContactId, "email",
            expectedAddresses[d.ContactId], "SM-2 message")).ToArray();

        await PatchEmail(both, "changed@example.com", "41");
        await PatchEmail(telegram, "added@example.com", "42");
        Assert.Equal(original.Select(Snapshot), (await ReadDraft()).Select(Snapshot));

        _sender.Handler = request => Result(request.ContactId == both);
        using var started = await client.PostAsync($"/api/campaigns/{id}/start", null);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Empty(_sender.Requests);
        using var logger = new LoggerConfiguration().CreateLogger();
        using var worker = new DeliveryWorker(_provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new DeliveryWorkerOptions { Parallelism = 1 }), logger, new WorkerIdentity("worker-A"));
        using var deadline = new CancellationTokenSource(Timeout);
        await worker.StartAsync(deadline.Token);
        try
        {
            while (!await db.Campaigns.AnyAsync(c => c.Id == id && c.Status == CampaignStatus.Completed, deadline.Token)
                || await db.Database.SqlQuery<int>($"""
                    SELECT count(*)::int AS "Value" FROM delivery_status_journal WHERE campaign_id = {id}
                    AND new_status IN ({(int)DeliveryStatus.Sent}, {(int)DeliveryStatus.Failed})
                    """).SingleAsync(deadline.Token) < expectedCalls.Length)
                await Task.Delay(25, deadline.Token);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var calls = _sender.Requests;
        var unique = calls.Select(c => c.DeliveryId).Distinct().Count();
        var outside = calls.Count(c => !expectedCalls.Contains(c));
        var final = await db.Deliveries.AsNoTracking().ToListAsync();
        var sent = final.Count(d => d.DeliveryStatus == DeliveryStatus.Sent);
        var failed = final.Count(d => d.DeliveryStatus == DeliveryStatus.Failed);
        output.WriteLine($"deliveries={final.Count} attempts={calls.Count} unique={unique} repeats={calls.Count - unique} outside={outside} sent={sent} failed={failed}");
        Assert.Equal(expectedCalls.OrderBy(c => c.DeliveryId), calls.OrderBy(c => c.DeliveryId));
        Assert.Equal((2, 1, 1), (final.Count, sent, failed));
        foreach (var delivery in original)
            await AssertTerminal(delivery, delivery.ContactId == both);

        async Task<Guid> CreateContact(params ContactChannelRequest[] channels)
        {
            using var response = await client.PostAsJsonAsync("/api/contacts", new { channels });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ContactResponse>())!.Id;
        }

        async Task PatchEmail(Guid contactId, string address, string chatId)
        {
            ContactChannelRequest[] channels = [new("email", address), new("telegram", chatId)];
            var path = $"/api/contacts/{contactId}";
            using var response = await client.PatchAsJsonAsync(path, new { channels });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = await client.GetFromJsonAsync<ContactResponse>(path);
            Assert.Equal(address, Assert.Single(saved!.Channels, c => c.Channel == "email").Address);
        }

        async Task<Delivery[]> ReadDraft()
        {
            Assert.Equal(selected.Order(), (await db.CampaignContacts.Where(c => c.CampaignId == id)
                .Select(c => c.ContactId).ToListAsync()).Order());
            Assert.Equal("email", await db.CampaignChannels.Where(c => c.CampaignId == id).Select(c => c.Channel).SingleAsync());
            var rows = await db.Deliveries.AsNoTracking().OrderBy(d => d.Id).ToArrayAsync();
            Assert.Equal(expectedAddresses.Keys.Order(), rows.Select(d => d.ContactId).Order());
            Assert.All(rows, d =>
            {
                Assert.Equal(id, d.CampaignId);
                Assert.Equal("email", d.Channel);
                Assert.Equal(expectedAddresses[d.ContactId], d.AddressSnapshot);
                Assert.Equal(DeliveryStatus.Waiting, d.DeliveryStatus);
            });
            return rows;
        }
    }
}
