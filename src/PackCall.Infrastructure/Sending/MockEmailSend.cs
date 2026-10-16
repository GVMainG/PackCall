using PackCall.Core.Sending;

namespace PackCall.Infrastructure.Sending;

internal sealed class MockEmailSend
{
    private MockEmailSend()
    {
    }

    private MockEmailSend(SendRequest request, SendResult result, DateTime createdAt)
    {
        DeliveryId = request.DeliveryId;
        CampaignId = request.CampaignId;
        RecipientId = request.RecipientId;
        RecipientEmail = request.RecipientEmail;
        MessageText = request.MessageText;
        IsSuccess = result.IsSuccess;
        Error = result.Error;
        CreatedAt = createdAt;
    }

    public Guid DeliveryId { get; private set; }
    public Guid CampaignId { get; private set; }
    public Guid RecipientId { get; private set; }
    public string RecipientEmail { get; private set; } = string.Empty;
    public string MessageText { get; private set; } = string.Empty;
    public bool IsSuccess { get; private set; }
    public string? Error { get; private set; }
    public DateTime CreatedAt { get; private set; }

    public static MockEmailSend Create(SendRequest request, SendResult result, DateTime createdAt) =>
        new(request, result, createdAt);

    public SendResult ToResult(bool isDuplicateSuppressed) =>
        new(IsSuccess, Error, isDuplicateSuppressed);
}
