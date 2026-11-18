using System.Data;
using Microsoft.EntityFrameworkCore;
using PackCall.Core;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;

namespace PackCall.Infrastructure.Repositories;

public sealed class CampaignRepository(AppDbContext dbContext, IDbContextFactory<AppDbContext> factory) : ICampaingRepository
{
    public Task<Campaign?> Get(Guid id, CancellationToken ct) =>
        dbContext.Campaigns
            .AsNoTracking()
            .SingleOrDefaultAsync(campaign => campaign.Id == id, ct);

    public async Task<CampaignDetails?> GetDetailsAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var campaign = await Get(id, ct);
        if (campaign is null)
            return null;

        var contacts = await dbContext.CampaignContacts.LongCountAsync(contact => contact.CampaignId == id, ct);
        var channels = await GetChannelsAsync(id, ct);
        var deliveries = await DeliveryRepository.ReadStatusCountsAsync(dbContext, id, ct);
        await transaction.CommitAsync(ct);
        return new CampaignDetails(campaign, deliveries, contacts, channels.Order(StringComparer.Ordinal).ToArray());
    }

    public async Task<IReadOnlyList<string>> GetChannelsAsync(Guid id, CancellationToken ct) =>
        await dbContext.CampaignChannels.Where(channel => channel.CampaignId == id)
            .Select(channel => channel.Channel).ToListAsync(ct);

    public async Task<PageResult<Campaign>> GetPage(
        int pageNumber,
        int pageSize,
        CancellationToken ct)
    {
        var skip = Pagination.GetOffset(pageNumber, pageSize);
        var query = dbContext.Campaigns.AsNoTracking();
        var totalCount = await query.CountAsync(ct);
        var items = await query.OrderBy(campaign => campaign.Id).Skip(skip).Take(pageSize).ToListAsync(ct);

        return new PageResult<Campaign>(items, totalCount);
    }

    public async Task<Campaign> Create(Campaign campaign, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Campaigns.AddAsync(campaign, ct);
        await db.SaveChangesAsync(ct);
        return campaign;
    }

    public async Task CreateWithSnapshotAsync(Campaign campaign, Guid[]? contactIds,
        IReadOnlyList<string> channels, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Campaigns.Add(campaign);
        db.CampaignChannels.AddRange(channels.Select(channel => new CampaignChannel(campaign.Id, channel)));
        await db.SaveChangesAsync(ct);

        if (contactIds is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO campaign_contacts (campaign_id, contact_id)
                SELECT {campaign.Id}, id FROM contacts
                """, ct);
        }
        else
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO campaign_contacts (campaign_id, contact_id)
                SELECT {campaign.Id}, id FROM contacts WHERE id = ANY({contactIds})
                """, ct);
            if (inserted != contactIds.Length)
                throw new ArgumentException("Один или несколько выбранных контактов не найдены.", nameof(contactIds));
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<Campaign> Update(Campaign campaign, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        if (dbContext.Entry(campaign).State == EntityState.Detached)
            dbContext.Campaigns.Update(campaign);

        await dbContext.SaveChangesAsync(ct);
        return campaign;
    }

    public async Task<bool> TryUpdateStatusAsync(Campaign campaign, CampaignStatus expectedStatus, CancellationToken ct)
    {
        var updated = await dbContext.Campaigns
            .Where(row => row.Id == campaign.Id && row.Status == expectedStatus)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, campaign.Status)
                .SetProperty(row => row.UpdatedAt, campaign.UpdatedAt)
                .SetProperty(row => row.StartedAt, campaign.StartedAt)
                .SetProperty(row => row.CompletedAt, campaign.CompletedAt), ct);
        return updated == 1;
    }

    public async Task<bool> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await dbContext.Campaigns
            .Where(campaign => campaign.Id == id)
            .ExecuteDeleteAsync(ct);

        return deleted > 0;
    }

    public async Task DeleteIfFormingAsync(Guid id, CancellationToken ct) =>
        await dbContext.Campaigns
            .Where(campaign => campaign.Id == id && campaign.Status == CampaignStatus.Forming)
            .ExecuteDeleteAsync(ct);
}
