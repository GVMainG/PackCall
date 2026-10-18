namespace PackCall.Core.Sending;

/// <summary>
/// Канал отправки сообщений получателям. В MVP единственная реализация — mock-email.
/// </summary>
public interface IMessageSender
{
    /// <summary>
    /// Передаёт сообщение провайдеру и ожидает его ответа.
    /// Успешный ответ не подтверждает доставку сообщения получателю.
    /// </summary>
    /// <param name="request">Данные отправки.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<SendResult> SendAsync(SendRequest request, CancellationToken ct);
}
