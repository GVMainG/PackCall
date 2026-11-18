using System.Net.Mail;
using PackCall.Core.Entities;
using PackCall.Core.Sending;

namespace PackCall.Infrastructure.Sending;

public interface IOperatorRandom { double NextDouble(); }
public sealed class OperatorRandom : IOperatorRandom
{
    public double NextDouble() => Random.Shared.NextDouble();
}

public sealed class MockEmailSender(IOperatorRandom random, bool simulateErrors) : IMessageSender
{
    public Task<SendResult> SendAsync(SendRequest request, CancellationToken ct)
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
        if (request is null || request.Channel != ContactChannel.Email
            || request.DeliveryId == Guid.Empty || request.CampaignId == Guid.Empty || request.ContactId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.MessageText)
            || string.IsNullOrEmpty(request.Address) || request.Address.Length > 255
            || request.Address.Contains('\r') || request.Address.Contains('\n')
            || !MailAddress.TryCreate(request.Address, out var address) || address.Address != request.Address)
            throw new TestOperatorValidationException();
    }
}
