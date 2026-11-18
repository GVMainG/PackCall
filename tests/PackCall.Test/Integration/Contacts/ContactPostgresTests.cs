using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Entities;
using PackCall.Core.Exceptions;
using PackCall.Core.Repositorys;
using PackCall.Infrastructure;

namespace PackCall.Core.Test;

[Trait("Category", "Integration")]
[Trait("Module", "Contacts")]
public class ContactPostgresTests : PostgresTestBase
{
    [PostgresFact]
    public async Task ConcurrentCreatesAcceptOnlyOneNormalizedAddress()
    {
        var accepted = await Task.WhenAll(TryCreateAsync(" +0042 "), TryCreateAsync("42"));
        Assert.Single(accepted, result => result);

        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var contact = await db.Contacts.Include(item => item.Channels).SingleAsync();
        Assert.Equal("42", Assert.Single(contact.Channels).Address);

        async Task<bool> TryCreateAsync(string address)
        {
            await using var attempt = _provider!.CreateAsyncScope();
            var repository = attempt.ServiceProvider.GetRequiredService<IContactRepository>();
            try
            {
                await repository.CreateAsync(Contact.Create([("telegram", address)], DateTime.UtcNow),
                    CancellationToken.None);
                return true;
            }
            catch (ContactAddressConflictException)
            {
                return false;
            }
        }
    }

    [PostgresFact]
    public async Task NormalizedAddressConflictPreservesStoredContact()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IContactRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ct = CancellationToken.None;
        var contact = await repository.CreateAsync(Contact.Create(
            [("email", "original@example.com"), ("telegram", "42")], DateTime.UtcNow), ct);
        await repository.CreateAsync(Contact.Create([("telegram", "-42")], DateTime.UtcNow), ct);

        await Assert.ThrowsAsync<ContactAddressConflictException>(() => repository.CreateAsync(
            Contact.Create([("email", " ORIGINAL@EXAMPLE.COM "), ("telegram", "100")], DateTime.UtcNow), ct));
        await Assert.ThrowsAsync<ContactAddressConflictException>(() => repository.ReplaceAddressesAsync(contact.Id,
            [("email", "new@example.com"), ("telegram", " -00042 ")], ct));

        Assert.Equal(2, await db.Contacts.CountAsync(ct));
        var stored = await db.ContactChannels.Where(c => c.ContactId == contact.Id).OrderBy(c => c.Channel)
            .Select(c => c.Address).ToArrayAsync(ct);
        Assert.Equal(new[] { "original@example.com", "42" }, stored);
        Assert.False(await db.ContactChannels.AnyAsync(c => c.Address == "100", ct));
    }

    [PostgresFact]
    public async Task ReplacingChannelsPreservesCampaignSelectionAndDeliverySnapshot()
    {
        var campaignId = await CreateStartedCampaignAsync();
        await using var scope = _provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<IContactRepository>();
        var ct = CancellationToken.None;
        var delivery = await db.Deliveries.AsNoTracking().SingleAsync(d => d.CampaignId == campaignId, ct);
        var contactId = delivery.ContactId;

        Assert.NotNull(await repository.ReplaceAddressesAsync(contactId, [("email", " New@Example.COM ")], ct));
        Assert.Equal("new@example.com", (await repository.GetAsync(contactId, ct))!.Email);
        Assert.NotNull(await repository.ReplaceAddressesAsync(contactId, [("telegram", " -030 ")], ct));

        var channel = await db.ContactChannels.AsNoTracking().SingleAsync(c => c.ContactId == contactId, ct);
        Assert.Equal(("telegram", "-30"), (channel.Channel, channel.Address));
        Assert.Null(await repository.GetAsync(contactId, ct));
        Assert.Equal(contactId, await db.CampaignContacts.Where(c => c.CampaignId == campaignId)
            .Select(c => c.ContactId).SingleAsync(ct));
        Assert.Equal("email", await db.CampaignChannels.Where(c => c.CampaignId == campaignId)
            .Select(c => c.Channel).SingleAsync(ct));
        var storedDelivery = await ReadDelivery(delivery.Id);
        Assert.Equal(delivery.Channel, storedDelivery.Channel);
        Assert.Equal(Snapshot(delivery), Snapshot(storedDelivery));
    }

    [PostgresFact]
    public async Task ConcurrentReplacementsApplyWholeSetsInOrder()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IContactRepository>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ct = CancellationToken.None;
        var contact = await repository.CreateAsync(Contact.Create(
            [("email", "original@example.com")], DateTime.UtcNow), ct);

        var results = await Task.WhenAll(
            repository.ReplaceAddressesAsync(contact.Id, [("telegram", "42")], ct),
            repository.ReplaceAddressesAsync(contact.Id, [("email", "new@example.com")], ct));

        Assert.Equal("42", Assert.Single(results[0]!.Channels).Address);
        Assert.Equal("new@example.com", Assert.Single(results[1]!.Channels).Address);
        var stored = await db.ContactChannels.AsNoTracking().SingleAsync(c => c.ContactId == contact.Id, ct);
        Assert.Contains((stored.Channel, stored.Address), new[] { ("email", "new@example.com"), ("telegram", "42") });
    }
}
