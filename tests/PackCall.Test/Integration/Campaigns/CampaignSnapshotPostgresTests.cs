using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using PackCall.Core.Services;
using PackCall.Infrastructure;

namespace PackCall.Core.Test;

[Trait("Category", "Integration")]
[Trait("Module", "Campaigns")]
public sealed class CampaignSnapshotPostgresTests : PostgresTestBase
{
    [PostgresFact]
    public async Task AllContactsSnapshotSurvivesNewContactAndAddressReplacement()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contacts = scope.ServiceProvider.GetRequiredService<IContactRepository>();
        var email = await campaigns.CreateRecipientAsync("before@example.com", default);
        var telegram = await contacts.CreateAsync(Contact.Create([(ContactChannel.Telegram, "123")], DateTime.UtcNow), default);
        var existingCampaign = await campaigns.CreateCampaignAsync("Existing", "Text", default);

        var id = await campaigns.CreateCampaignSnapshotAsync("All", "Text", null, [ContactChannel.Email], default);
        await using var observer = _provider!.CreateAsyncScope();
        var db = observer.ServiceProvider.GetRequiredService<AppDbContext>();
        await AssertSnapshot(db, id, [email.Id, telegram.Id], [ContactChannel.Email]);
        var existingDelivery = await db.Deliveries.AsNoTracking().SingleAsync(d => d.CampaignId == existingCampaign);
        Assert.Equal("before@example.com", existingDelivery.AddressSnapshot);

        await campaigns.CreateRecipientAsync("new@example.com", default);
        var changed = await contacts.ReplaceAddressesAsync(email.Id, [(ContactChannel.Email, "after@example.com")], default);
        Assert.Equal("after@example.com", changed?.Email);

        await AssertSnapshot(db, id, [email.Id, telegram.Id], [ContactChannel.Email]);
        var persistedDelivery = await db.Deliveries.AsNoTracking().SingleAsync();
        Assert.Equal(existingDelivery.Id, persistedDelivery.Id);
        Assert.Equal(existingDelivery.AddressSnapshot, persistedDelivery.AddressSnapshot);
    }

    [PostgresFact]
    public async Task ExplicitSnapshotStoresOnlySelectedContactsAndDistinctChannels()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var chosen = await campaigns.CreateRecipientAsync("chosen@example.com", default);
        await campaigns.CreateRecipientAsync("excluded@example.com", default);

        var id = await campaigns.CreateCampaignSnapshotAsync("Explicit", "Text", [chosen.Id, chosen.Id],
            [ContactChannel.Telegram, ContactChannel.Email, ContactChannel.Telegram], default);

        await using var observer = _provider!.CreateAsyncScope();
        var db = observer.ServiceProvider.GetRequiredService<AppDbContext>();
        await AssertSnapshot(db, id, [chosen.Id], [ContactChannel.Email, ContactChannel.Telegram]);

        var reader = observer.ServiceProvider.GetRequiredService<ICampaignService>();
        var persisted = (await reader.GetCampaignAsync(id, default))!;
        Assert.Equal("Explicit", persisted.Name);
        Assert.Equal("Text", persisted.MessageText);
        persisted.StatusFromFormingToDraft();
        var repository = observer.ServiceProvider.GetRequiredService<ICampaingRepository>();
        Assert.True(await repository.TryUpdateStatusAsync(persisted, CampaignStatus.Forming, default));
        await campaigns.UpdateCampaignAsync(id, "Edited", "Updated text", default);
        await AssertSnapshot(db, id, [chosen.Id], [ContactChannel.Email, ContactChannel.Telegram], CampaignStatus.Draft);
        Assert.True(await campaigns.DeleteCampaignAsync(id, default));
        await AssertNoCampaignData(db);
    }

    [PostgresFact]
    public async Task ExplicitSnapshotKeepsContactWithoutAddressInSelectedChannel()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await campaigns.CreateRecipientAsync("email-only@example.com", default);
        var id = await campaigns.CreateCampaignSnapshotAsync("Telegram", "Text", [contact.Id], [ContactChannel.Telegram], default);

        await using var observer = _provider!.CreateAsyncScope();
        await AssertSnapshot(observer.ServiceProvider.GetRequiredService<AppDbContext>(), id, [contact.Id], [ContactChannel.Telegram]);
    }

    [PostgresFact]
    public async Task AllContactsAllowsEmptyDirectory()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var id = await campaigns.CreateCampaignSnapshotAsync("Empty directory", "Text", null, [ContactChannel.Email], default);

        await using var observer = _provider!.CreateAsyncScope();
        await AssertSnapshot(observer.ServiceProvider.GetRequiredService<AppDbContext>(), id, [], [ContactChannel.Email]);
    }

    [PostgresFact]
    public async Task InvalidSelectionsLeaveNoCampaignData()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var emptyContacts = await Assert.ThrowsAsync<ArgumentException>(() =>
            campaigns.CreateCampaignSnapshotAsync("Invalid", "Text", [], [ContactChannel.Email], default));
        Assert.Equal("contactIds", emptyContacts.ParamName);
        await AssertNoCampaignData(db);

        foreach (string?[] channels in new string?[][] { [], ["sms"], ["Email"], [null] })
        {
            var invalidChannels = await Assert.ThrowsAsync<ArgumentException>(() =>
                campaigns.CreateCampaignSnapshotAsync("Invalid", "Text", null, channels, default));
            Assert.Equal("channels", invalidChannels.ParamName);
            await AssertNoCampaignData(db);
        }
    }

    [PostgresFact]
    public async Task MissingExplicitContactRollsBackAndAllowsAnotherCreationInTheSameScope()
    {
        await using var scope = _provider!.CreateAsyncScope();
        var campaigns = scope.ServiceProvider.GetRequiredService<ICampaignService>();
        var contact = await campaigns.CreateRecipientAsync("found@example.com", default);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => campaigns.CreateCampaignSnapshotAsync(
            "Missing contact", "Text", [contact.Id, Guid.NewGuid()], [ContactChannel.Email, ContactChannel.Telegram], default));
        Assert.Equal("contactIds", error.ParamName);
        Assert.Contains("не найдены", error.Message);

        await using var observer = _provider!.CreateAsyncScope();
        var db = observer.ServiceProvider.GetRequiredService<AppDbContext>();
        await AssertNoCampaignData(db);
        var id = await campaigns.CreateCampaignSnapshotAsync("Valid", "Text", [contact.Id], [ContactChannel.Email], default);
        await AssertSnapshot(db, id, [contact.Id], [ContactChannel.Email]);
        Assert.Equal(1, await db.Campaigns.CountAsync());
    }

    private static async Task AssertSnapshot(AppDbContext db, Guid id, Guid[] contacts, string[] channels,
        CampaignStatus status = CampaignStatus.Forming)
    {
        Assert.Equal(status, await db.Campaigns.Where(c => c.Id == id).Select(c => c.Status).SingleAsync());
        var actualContacts = await db.CampaignContacts.Where(c => c.CampaignId == id).Select(c => c.ContactId).ToArrayAsync();
        Assert.Equal(contacts.Order(), actualContacts.Order());
        var actualChannels = await db.CampaignChannels.Where(c => c.CampaignId == id).Select(c => c.Channel).ToArrayAsync();
        Assert.Equal(channels.Order(StringComparer.Ordinal), actualChannels.Order(StringComparer.Ordinal));
        Assert.False(await db.Deliveries.AnyAsync(d => d.CampaignId == id));
    }

    private static async Task AssertNoCampaignData(AppDbContext db)
    {
        Assert.False(await db.Campaigns.AnyAsync());
        Assert.False(await db.CampaignContacts.AnyAsync());
        Assert.False(await db.CampaignChannels.AnyAsync());
        Assert.False(await db.Deliveries.AnyAsync());
    }
}
