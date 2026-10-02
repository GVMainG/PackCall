namespace PackCall.Core.Sending;

/// <summary>
/// Канал отправки сообщений получателям. В MVP единственная реализация — mock-email.
/// </summary>
public interface IMessageSender
{
    /// <summary>
    /// Отправляет сообщение получателю.
    /// </summary>
    /// <param name="request">Данные отправки; <see cref="SendRequest.DeliveryId"/> — ключ идемпотентности.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<SendResult> SendAsync(SendRequest request, CancellationToken ct);
}
