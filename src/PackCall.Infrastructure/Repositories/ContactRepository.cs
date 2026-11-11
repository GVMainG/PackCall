using Microsoft.EntityFrameworkCore;
using PackCall.Core.Entities;
using PackCall.Core;
using PackCall.Core.Repositorys;
using PackCall.Core.Exceptions;
using Npgsql;

namespace PackCall.Infrastructure.Repositories;

public sealed class ContactRepository(AppDbContext dbContext) : IContactRepository
{
    public async Task<Contact> CreateAsync(Contact recipient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        await dbContext.Contacts.AddAsync(recipient, ct);
        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_contact_channels_channel_address" })
        {
            throw new RecipientAlreadyExistsException(exception);
        }
        return recipient;
    }

    public Task<Contact?> GetByEmailAsync(string email, CancellationToken ct)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        return dbContext.Contacts.AsNoTracking().Include(contact => contact.Channels)
            .SingleOrDefaultAsync(contact => contact.Channels.Any(channel =>
                channel.Channel == "email" && channel.Address == normalizedEmail), ct);
    }

    public async Task<IReadOnlyList<Contact>> GetBatchAsync(int offset, int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return await EmailContacts().Include(contact => contact.Channels)
            .OrderBy(recipient => recipient.Id)
            .Skip(offset).Take(batchSize).ToListAsync(ct);
    }

    public Task<Contact?> GetAsync(Guid id, CancellationToken ct) =>
        EmailContacts().Include(contact => contact.Channels).SingleOrDefaultAsync(recipient => recipient.Id == id, ct);

    public async Task<PageResult<Contact>> GetPageAsync(int pageNumber, int pageSize, CancellationToken ct)
    {
        var skip = Pagination.GetOffset(pageNumber, pageSize);
        var query = EmailContacts();
        var totalCount = await query.CountAsync(ct);
        var items = await query.Include(contact => contact.Channels)
            .OrderBy(recipient => recipient.Id).Skip(skip).Take(pageSize).ToListAsync(ct);
        return new PageResult<Contact>(items, totalCount);
    }

    private IQueryable<Contact> EmailContacts() =>
        dbContext.Contacts.AsNoTracking().Where(contact => contact.Channels.Any(channel => channel.Channel == "email"));
}
