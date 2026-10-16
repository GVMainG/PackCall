using Microsoft.EntityFrameworkCore;
using PackCall.Core.Entities;
using PackCall.Core;
using PackCall.Core.Repositorys;

namespace PackCall.Infrastructure.Repositories;

public sealed class RecipientRepository(AppDbContext dbContext) : IRecipientRepository
{
    public async Task<IReadOnlyList<Recipient>> GetAllAsync(CancellationToken ct) =>
        await dbContext.Recipients
            .OrderBy(recipient => recipient.Id)
            .ToListAsync(ct);

    public Task<Recipient?> GetAsync(Guid id, CancellationToken ct) =>
        dbContext.Recipients.AsNoTracking().SingleOrDefaultAsync(recipient => recipient.Id == id, ct);

    public async Task<PageResult<Recipient>> GetPageAsync(int pageNumber, int pageSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        int skip;
        try
        {
            skip = checked((pageNumber - 1) * pageSize);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), exception);
        }
        var query = dbContext.Recipients.AsNoTracking().OrderBy(recipient => recipient.Id);
        var totalCount = await query.CountAsync(ct);
        var items = await query.Skip(skip).Take(pageSize).ToListAsync(ct);
        return new PageResult<Recipient>(items, totalCount);
    }
}
