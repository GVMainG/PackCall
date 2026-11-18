using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using PackCall.Core.Services;
using PackCall.Infrastructure;

namespace PackCall.Core.Test;

[Trait("Category", "Integration")]
[Trait("Module", "Campaigns")]
public sealed class CampaignFormationPostgresTests : PostgresTestBase
{
    [PostgresFact]
    public async Task FormsExactlyTheSelectedPairsWithAddresses()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contacts = scope.ServiceProvider.GetRequiredService<IContactRepository>();
        var both = await contacts.CreateAsync(Contact.Create(
            [(ContactChannel.Email, " Both@Example.com "), (ContactChannel.Telegram, " +00042 ")], DateTime.UtcNow), default);
        var email = await campaigns.CreateRecipientAsync("email@example.com", default);
        var telegram = await contacts.CreateAsync(Contact.Create(
            [(ContactChannel.Telegram, "-123")], DateTime.UtcNow), default);
        await campaigns.CreateRecipientAsync("outside@example.com", default);

        Guid[] selected = [both.Id, email.Id, telegram.Id];
        var id = await campaigns.CreateCampaignSnapshotAsync("Selected", "Text", selected,
            [ContactChannel.Email, ContactChannel.Telegram], default);

        Assert.Equal(4, await campaigns.FormDeliveriesFromSnapshotAsync(id, default));

        await using var observer = _provider!.CreateAsyncScope();
        var db = observer.ServiceProvider.GetRequiredService<AppDbContext>();
        var deliveries = await ReadDeliveries(db, id);
        (Guid Contact, string Channel, string? Address)[] expected =
        [
            (both.Id, ContactChannel.Email, "both@example.com"),
            (both.Id, ContactChannel.Telegram, "42"),
            (email.Id, ContactChannel.Email, "email@example.com"),
            (telegram.Id, ContactChannel.Telegram, "-123")
        ];
        Assert.Equal(expected.Order(), deliveries.Select(d => (d.ContactId, d.Channel, d.AddressSnapshot)).Order());
        Assert.Equal(deliveries.Length, deliveries.Select(d => d.Id).Distinct().Count());
        Assert.All(deliveries, delivery =>
        {
            Assert.NotEqual(Guid.Empty, delivery.Id);
            Assert.Equal(DeliveryStatus.Waiting, delivery.DeliveryStatus);
            Assert.True(delivery.CreatedAt > DateTime.UnixEpoch);
            Assert.Equal(delivery.CreatedAt, delivery.UpdatedAt);
            Assert.Null(delivery.WorkerId);
            Assert.Null(delivery.LockExpiresAt);
            Assert.Null(delivery.CompletedAt);
        });
        var audience = await db.CampaignContacts.Where(c => c.CampaignId == id).Select(c => c.ContactId).ToListAsync();
        Assert.Equal(selected.Order(), audience.Order());
        var channels = await db.CampaignChannels.Where(c => c.CampaignId == id).Select(c => c.Channel).ToListAsync();
        Assert.Equal(new[] { ContactChannel.Email, ContactChannel.Telegram }, channels.Order());
        Assert.Equal(CampaignStatus.Forming, (await campaigns.GetCampaignAsync(id, default))!.Status);
        Assert.Empty(_sender.Requests);
    }

    [PostgresFact]
    public async Task StoresAddressFromFormationAndExcludesUnselectedChannel()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contacts = scope.ServiceProvider.GetRequiredService<IContactRepository>();
        var contact = await contacts.CreateAsync(Contact.Create(
            [(ContactChannel.Email, "before@example.com"), (ContactChannel.Telegram, "42")], DateTime.UtcNow), default);
        var id = await campaigns.CreateCampaignSnapshotAsync("Email", "Text", [contact.Id], [ContactChannel.Email], default);
        await contacts.ReplaceAddressesAsync(contact.Id,
            [(ContactChannel.Email, " AtFormation@Example.com "), (ContactChannel.Telegram, "42")], default);

        Assert.Equal(1, await campaigns.FormDeliveriesFromSnapshotAsync(id, default));

        await using var observer = _provider!.CreateAsyncScope();
        var db = observer.ServiceProvider.GetRequiredService<AppDbContext>();
        var delivery = Assert.Single(await ReadDeliveries(db, id));
        Assert.Equal(contact.Id, delivery.ContactId);
        Assert.Equal(ContactChannel.Email, delivery.Channel);
        Assert.Equal("atformation@example.com", delivery.AddressSnapshot);

        await contacts.ReplaceAddressesAsync(contact.Id, [(ContactChannel.Telegram, "42")], default);
        var stored = Assert.Single(await ReadDeliveries(db, id));
        Assert.Equal(delivery.Id, stored.Id);
        Assert.Equal("atformation@example.com", stored.AddressSnapshot);
        Assert.Empty(_sender.Requests);
    }

    [PostgresFact]
    public async Task FormationReturnsZeroWhenNoSelectedContactHasAnAddressInSelectedChannel()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await campaigns.CreateRecipientAsync("email@example.com", default);
        var id = await campaigns.CreateCampaignSnapshotAsync("Telegram", "Text", [contact.Id], [ContactChannel.Telegram], default);

        Assert.Equal(0, await campaigns.FormDeliveriesFromSnapshotAsync(id, default));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await ReadDeliveries(db, id));
        Assert.Equal(contact.Id, await db.CampaignContacts.Where(c => c.CampaignId == id).Select(c => c.ContactId).SingleAsync());
    }

    [PostgresFact]
    public async Task RejectsMissingCampaignAndCampaignOutsideForming()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => campaigns.FormDeliveriesFromSnapshotAsync(Guid.NewGuid(), default));

        var contact = await campaigns.CreateRecipientAsync("email@example.com", default);
        var id = await campaigns.CreateCampaignSnapshotAsync("Draft", "Text", [contact.Id], [ContactChannel.Email], default);
        var campaign = (await campaigns.GetCampaignAsync(id, default))!;
        campaign.StatusFromFormingToDraft();
        var repository = scope.ServiceProvider.GetRequiredService<ICampaingRepository>();
        Assert.True(await repository.TryUpdateStatusAsync(campaign, CampaignStatus.Forming, default));

        await Assert.ThrowsAsync<InvalidOperationException>(() => campaigns.FormDeliveriesFromSnapshotAsync(id, default));
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Deliveries.AnyAsync());
        Assert.Equal(CampaignStatus.Draft, (await campaigns.GetCampaignAsync(id, default))!.Status);
    }

    [PostgresFact]
    public async Task DuplicatePairRollsBackNewlyEligiblePairs()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contacts = scope.ServiceProvider.GetRequiredService<IContactRepository>();
        var first = await campaigns.CreateRecipientAsync("first@example.com", default);
        var second = await contacts.CreateAsync(Contact.Create(
            [(ContactChannel.Telegram, "42")], DateTime.UtcNow), default);
        var id = await campaigns.CreateCampaignSnapshotAsync("Duplicate", "Text", [first.Id, second.Id], [ContactChannel.Email], default);
        Assert.Equal(1, await campaigns.FormDeliveriesFromSnapshotAsync(id, default));

        await using var observer = _provider!.CreateAsyncScope();
        var db = observer.ServiceProvider.GetRequiredService<AppDbContext>();
        var original = Assert.Single(await ReadDeliveries(db, id));
        await contacts.ReplaceAddressesAsync(second.Id, [(ContactChannel.Email, "now-eligible@example.com")], default);

        var error = await Assert.ThrowsAsync<PostgresException>(() => campaigns.FormDeliveriesFromSnapshotAsync(id, default));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("ix_deliveries_campaign_contact_channel", error.ConstraintName);
        var persisted = Assert.Single(await ReadDeliveries(db, id));
        Assert.Equal(original.Id, persisted.Id);
        Assert.Equal(first.Id, persisted.ContactId);
        Assert.Equal(ContactChannel.Email, persisted.Channel);
        Assert.Equal("first@example.com", persisted.AddressSnapshot);
        Assert.Empty(_sender.Requests);
    }

    private static Task<Delivery[]> ReadDeliveries(AppDbContext db, Guid campaignId) =>
        db.Deliveries.AsNoTracking().Where(d => d.CampaignId == campaignId).ToArrayAsync();
}
