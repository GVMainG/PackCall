using Microsoft.EntityFrameworkCore;
using PackCall.Core.Entities;
using PackCall.Core;
using PackCall.Core.Repositorys;
using PackCall.Core.Exceptions;
using Npgsql;

namespace PackCall.Infrastructure.Repositories;

public sealed class RecipientRepository(AppDbContext dbContext) : IRecipientRepository
{
    public async Task<Recipient> CreateAsync(Recipient recipient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        await dbContext.Recipients.AddAsync(recipient, ct);
        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_recipients_email" })
        {
            throw new RecipientAlreadyExistsException(exception);
        }
        return recipient;
    }

    public Task<Recipient?> GetByEmailAsync(string email, CancellationToken ct) =>
        dbContext.Recipients.AsNoTracking()
            .FirstOrDefaultAsync(recipient => recipient.Email.ToLower() == email, ct);

    public async Task<IReadOnlyList<Recipient>> GetBatchAsync(int offset, int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return await dbContext.Recipients.AsNoTracking()
            .OrderBy(recipient => recipient.Id)
            .Skip(offset).Take(batchSize).ToListAsync(ct);
    }

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
