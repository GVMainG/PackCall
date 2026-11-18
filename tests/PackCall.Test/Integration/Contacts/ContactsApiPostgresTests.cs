using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Api.Controllers;
using PackCall.Api.Models;
using PackCall.Infrastructure;

namespace PackCall.Core.Test;

[Trait("Category", "Integration")]
[Trait("Module", "Contacts")]
public sealed class ContactsApiPostgresTests : PostgresTestBase
{
    [PostgresFact]
    public async Task CreatesReadsAndPaginatesAllChannels()
    {
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        using var response = await client.PostAsJsonAsync("/api/contacts", Request("telegram", " +00042 "));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ContactResponse>())!;
        Assert.Equal("Contact", created.DisplayName);
        Assert.Equal(new ContactChannelResponse("telegram", "42"), Assert.Single(created.Channels));
        Assert.Equal($"/api/contacts/{created.Id}", response.Headers.Location?.AbsolutePath);
        var loaded = await client.GetFromJsonAsync<ContactResponse>(response.Headers.Location);
        Assert.Equal(created.Id, loaded!.Id);
        Assert.Equal(created.Channels.ToArray(), loaded.Channels.ToArray());

        var page = (await client.GetFromJsonAsync<PageResponse<ContactResponse>>("/api/contacts"))!;
        Assert.Equal((1, 20, 1, 1), (page.PageNumber, page.PageSize, page.TotalCount, page.TotalPages));
        Assert.Equal(created.Id, Assert.Single(page.Items).Id);
        var distant = (await client.GetFromJsonAsync<PageResponse<ContactResponse>>(
            "/api/contacts?pageNumber=2147483647&pageSize=100"))!;
        Assert.Empty(distant.Items);
        Assert.Equal(1, distant.TotalCount);
        var recipients = (await client.GetFromJsonAsync<PageResponse<RecipientResponse>>("/api/recipients"))!;
        Assert.Empty(recipients.Items);

        using var second = await client.PostAsJsonAsync("/api/contacts", new CreateContactRequest(
            [new("email", " User@Example.COM "), new("telegram", "-9223372036854775808")]));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var other = (await second.Content.ReadFromJsonAsync<ContactResponse>())!;
        Assert.Equal(new[] { new ContactChannelResponse("email", "user@example.com"),
            new ContactChannelResponse("telegram", "-9223372036854775808") }, other.Channels.ToArray());
        var firstPage = (await client.GetFromJsonAsync<PageResponse<ContactResponse>>("/api/contacts?pageSize=1"))!;
        var secondPage = (await client.GetFromJsonAsync<PageResponse<ContactResponse>>(
            "/api/contacts?pageNumber=2&pageSize=1"))!;
        Assert.Equal((2, 2), (secondPage.TotalCount, secondPage.TotalPages));
        Assert.Equal(new[] { created.Id, other.Id }.Order(),
            new[] { Assert.Single(firstPage.Items).Id, Assert.Single(secondPage.Items).Id });
    }

    [PostgresFact]
    public async Task PatchPreservesDeliverySnapshotAndRejectedWritesPreserveAddresses()
    {
        var campaignId = await CreateStartedCampaignAsync();
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = await db.Deliveries.AsNoTracking().SingleAsync(item => item.CampaignId == campaignId);
        await db.Contacts.Where(item => item.Id == original.ContactId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DisplayName, "Original"));
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        var path = $"/api/contacts/{original.ContactId}";
        using var occupied = await client.PostAsJsonAsync("/api/contacts", Request("telegram", "123"));
        Assert.Equal(HttpStatusCode.Created, occupied.StatusCode);

        using var patch = await client.PatchAsJsonAsync(path, Replacement("-42"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var updated = await patch.Content.ReadFromJsonAsync<ContactResponse>();
        Assert.Equal(original.ContactId, updated!.Id);
        Assert.Equal("Original", updated.DisplayName);
        Assert.Equal(new ContactChannelResponse("telegram", "-42"), Assert.Single(updated.Channels));
        using var duplicate = await client.PostAsJsonAsync("/api/contacts", Request("telegram", "+00123"));
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict);
        using var emptyPatch = await client.PatchAsJsonAsync(path, new ReplaceContactAddressesRequest([]));
        await AssertProblemAsync(emptyPatch, HttpStatusCode.BadRequest);
        using var conflict = await client.PatchAsJsonAsync(path, Replacement("+00123"));
        await AssertProblemAsync(conflict, HttpStatusCode.Conflict);

        // Имитируем SQLSTATE прерванной записи на реальном пути EF и middleware.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION simulate_contact_deadlock() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'internal deadlock fixture' USING ERRCODE = '40P01';
            END; $$;
            CREATE TRIGGER simulate_contact_deadlock BEFORE UPDATE ON contact_channels
                FOR EACH ROW EXECUTE FUNCTION simulate_contact_deadlock();
            """);
        using var transient = await client.PatchAsJsonAsync(path, Replacement("-43"));
        await AssertProblemAsync(transient, HttpStatusCode.ServiceUnavailable);
        Assert.DoesNotContain("internal deadlock fixture", await transient.Content.ReadAsStringAsync());
        var stored = await client.GetFromJsonAsync<ContactResponse>(path);
        Assert.Equal(new ContactChannelResponse("telegram", "-42"), Assert.Single(stored!.Channels));
        var delivery = await ReadDelivery(original.Id);
        Assert.Equal(original.Channel, delivery.Channel);
        Assert.Equal(Snapshot(original), Snapshot(delivery));
    }

    [PostgresFact]
    public async Task RejectsInvalidInputAndReturnsProblemForMissingContacts()
    {
        await using var app = await StartApiAsync();
        using var client = CreateClient(app);
        using var empty = await client.PostAsJsonAsync("/api/contacts", new CreateContactRequest([]));
        await AssertProblemAsync(empty, HttpStatusCode.BadRequest);
        using var invalid = await client.PostAsJsonAsync("/api/contacts", Request("telegram", "0"));
        await AssertProblemAsync(invalid, HttpStatusCode.BadRequest);
        using var nullElement = await client.PostAsJsonAsync("/api/contacts", new CreateContactRequest([null]));
        await AssertProblemAsync(nullElement, HttpStatusCode.BadRequest);
        using var name = await client.PostAsJsonAsync("/api/contacts",
            Request("telegram", "42") with { DisplayName = new string('a', 256) });
        await AssertProblemAsync(name, HttpStatusCode.BadRequest);
        using var size = await client.GetAsync("/api/contacts?pageSize=101");
        await AssertProblemAsync(size, HttpStatusCode.BadRequest);
        using var page = await client.GetAsync("/api/contacts?pageNumber=0");
        await AssertProblemAsync(page, HttpStatusCode.BadRequest);
        using var invalidId = await client.GetAsync("/api/contacts/not-a-guid");
        await AssertProblemAsync(invalidId, HttpStatusCode.BadRequest);
        var path = $"/api/contacts/{Guid.NewGuid()}";
        using var missing = await client.GetAsync(path);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);
        using var patch = await client.PatchAsJsonAsync(path, Replacement("42"));
        await AssertProblemAsync(patch, HttpStatusCode.NotFound);
    }

    private static CreateContactRequest Request(string channel, string address) =>
        new([new ContactChannelRequest(channel, address)], "Contact");

    private static ReplaceContactAddressesRequest Replacement(string address) =>
        new([new ContactChannelRequest("telegram", address)]);

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
    }

    private async Task<WebApplication> StartApiAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        AddServices(builder.Services);
        builder.Services.AddSingleton<Serilog.ILogger>(Serilog.Log.Logger);
        builder.Services.AddControllers().AddApplicationPart(typeof(ContactsController).Assembly);
        builder.Services.AddProblemDetails();
        var handler = typeof(ContactsController).Assembly.GetType(
            "PackCall.Api.Middleware.ApiExceptionHandler", throwOnError: true)!;
        builder.Services.AddSingleton(typeof(IExceptionHandler), handler);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        return new HttpClient { BaseAddress = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    }
}
