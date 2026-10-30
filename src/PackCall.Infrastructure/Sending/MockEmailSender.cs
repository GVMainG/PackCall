using System.Net.Mail;
using PackCall.Core.Sending;

namespace PackCall.Infrastructure.Sending;

public interface IOperatorRandom { double NextDouble(); }
public sealed class OperatorRandom : IOperatorRandom
{
    public double NextDouble() => Random.Shared.NextDouble();
}

public sealed class MockEmailSender(IOperatorRandom random) : IMessageSender
{
    public Task<SendResult> SendAsync(SendRequest request, bool simulateErrors, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Validate(request);
        var result = simulateErrors && random.NextDouble() < 0.01
            ? SendResult.Failure("SimulatedOperatorRejected") : SendResult.Success();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    internal static void Validate(SendRequest request)
    {
        if (request is null || request.DeliveryId == Guid.Empty || request.CampaignId == Guid.Empty
            || request.RecipientId == Guid.Empty || string.IsNullOrWhiteSpace(request.MessageText)
            || string.IsNullOrEmpty(request.RecipientEmail) || request.RecipientEmail.Length > 255
            || request.RecipientEmail.Contains('\r') || request.RecipientEmail.Contains('\n')
            || !MailAddress.TryCreate(request.RecipientEmail, out var address) || address.Address != request.RecipientEmail)
            throw new TestOperatorValidationException();
    }
}
