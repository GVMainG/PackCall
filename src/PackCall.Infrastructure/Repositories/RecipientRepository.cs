using Microsoft.EntityFrameworkCore;
using PackCall.Core.Entities;
using PackCall.Core.Repositorys;

namespace PackCall.Infrastructure.Repositories;

public sealed class RecipientRepository(AppDbContext dbContext) : IRecipientRepository
{
    public async Task<IReadOnlyList<Recipient>> GetAllAsync(CancellationToken ct) =>
        await dbContext.Recipients
            .OrderBy(recipient => recipient.Id)
            .ToListAsync(ct);
}
