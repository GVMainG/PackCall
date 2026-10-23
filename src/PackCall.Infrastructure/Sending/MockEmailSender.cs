using System.Net.Mail;
using Microsoft.Extensions.Options;
using PackCall.Core.Sending;

namespace PackCall.Infrastructure.Sending;

/// <summary>
/// Имитирует ответ провайдера на отправленное сообщение.
/// Успех означает подтверждение приёма сообщения, а не доставку адресату.
/// </summary>
public sealed class MockEmailSender : IMessageSender
{
    private readonly MockEmailOptions _options;

    public MockEmailSender(IOptions<MockEmailOptions> options)
    {
        _options = options.Value;

        if (_options.Delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Задержка mock-email не может быть отрицательной.");
        if (string.IsNullOrWhiteSpace(_options.FailureMessage))
            throw new ArgumentException("Текст ошибки mock-email не может быть пустым.", nameof(options));
    }

    public async Task<SendResult> SendAsync(SendRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (_options.Delay > TimeSpan.Zero)
            await Task.Delay(_options.Delay, ct);

        return BuildResult(request);
    }

    private SendResult BuildResult(SendRequest request)
    {
        if (!MailAddress.TryCreate(request.RecipientEmail, out _))
            return SendResult.Failure("Mock-email отклонил некорректный адрес получателя.");

        return _options.FailedRecipients.Contains(request.RecipientEmail)
            ? SendResult.Failure(_options.FailureMessage)
            : SendResult.Success();
    }
}
