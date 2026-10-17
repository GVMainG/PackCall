using Microsoft.EntityFrameworkCore;
using PackCall.Core;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;
using System.Linq.Expressions;

namespace PackCall.Infrastructure.Repositories;

public sealed class CampaignRepository(AppDbContext dbContext) : ICampaingRepository
{
    public async Task<IEnumerable<Campaign>> Get(Guid id, CancellationToken ct) =>
        await dbContext.Campaigns
            .Where(campaign => campaign.Id == id)
            .ToListAsync(ct);

    public async Task<IEnumerable<Campaign>> Get(
        Expression<Func<Campaign, bool>> predicate,
        CancellationToken ct) =>
        await dbContext.Campaigns
            .Where(predicate)
            .ToListAsync(ct);

    public async Task<PageResult<Campaign>> GetPage(
        int pageNumber,
        int pageSize,
        CancellationToken ct)
    {
        var skip = GetSkip(pageNumber, pageSize);
        var query = dbContext.Campaigns.OrderBy(campaign => campaign.Id);
        var totalCount = await query.CountAsync(ct);
        var items = await query.Skip(skip).Take(pageSize).ToListAsync(ct);

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

    public async Task<bool> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await dbContext.Campaigns
            .Where(campaign => campaign.Id == id)
            .ExecuteDeleteAsync(ct);

        return deleted > 0;
    }

    private static int GetSkip(int pageNumber, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        try
        {
            return checked((pageNumber - 1) * pageSize);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), exception);
        }
    }
}
