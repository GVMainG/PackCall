namespace PackCall.Core.Sending;

/// <summary>
/// Канал отправки сообщений получателям. Тестовый оператор выбирается при регистрации приложения.
/// </summary>
public interface IMessageSender
{
    /// <summary>
    /// Передаёт сообщение провайдеру и ожидает его ответа.
    /// Успешный ответ не подтверждает доставку сообщения получателю.
    /// </summary>
    /// <param name="request">Данные отправки.</param>
    /// <param name="simulateErrors">Включить независимые случайные отказы оператора.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<SendResult> SendAsync(SendRequest request, bool simulateErrors, CancellationToken ct);
}
