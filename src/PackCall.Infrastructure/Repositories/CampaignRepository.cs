using Microsoft.EntityFrameworkCore;
using PackCall.Core;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;

namespace PackCall.Infrastructure.Repositories;

public sealed class CampaignRepository(AppDbContext dbContext) : ICampaingRepository
{
    public Task<Campaign?> Get(Guid id, CancellationToken ct) =>
        dbContext.Campaigns
            .AsNoTracking()
            .SingleOrDefaultAsync(campaign => campaign.Id == id, ct);

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

        await dbContext.Campaigns.AddAsync(campaign, ct);
        await dbContext.SaveChangesAsync(ct);
        return campaign;
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
}
