using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PackCall.Core.Sending;

namespace PackCall.Infrastructure.Sending;

/// <summary>
/// Идемпотентный mock-email канал. Первый результат сохраняется в PostgreSQL
/// и возвращается для всех повторных вызовов с тем же DeliveryId.
/// </summary>
public sealed class MockEmailSender : IMessageSender
{
    private const string PrimaryKeyName = "pk_mock_email_sends_delivery_id";

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly MockEmailOptions _options;

    public MockEmailSender(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOptions<MockEmailOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _options = options.Value;

        if (_options.Delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Задержка mock-email не может быть отрицательной.");
        if (string.IsNullOrWhiteSpace(_options.FailureMessage))
            throw new ArgumentException("Текст ошибки mock-email не может быть пустым.", nameof(options));
    }

    public async Task<SendResult> SendAsync(SendRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_options.Delay > TimeSpan.Zero)
            await Task.Delay(_options.Delay, ct);

        var result = BuildResult(request);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
        dbContext.MockEmailSends.Add(MockEmailSend.Create(request, result, DateTime.UtcNow));

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return result;
        }
        catch (DbUpdateException exception) when (IsDuplicateDelivery(exception))
        {
            await using var readContext = await _dbContextFactory.CreateDbContextAsync(ct);
            var original = await readContext.MockEmailSends
                .AsNoTracking()
                .SingleAsync(send => send.DeliveryId == request.DeliveryId, ct);

            return original.ToResult(isDuplicateSuppressed: true);
        }
    }

    private SendResult BuildResult(SendRequest request)
    {
        if (!MailAddress.TryCreate(request.RecipientEmail, out _))
            return SendResult.Failure("Mock-email отклонил некорректный адрес получателя.");

        return _options.FailedRecipients.Contains(request.RecipientEmail)
            ? SendResult.Failure(_options.FailureMessage)
            : SendResult.Success();
    }

    private static bool IsDuplicateDelivery(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PrimaryKeyName
        };
}
