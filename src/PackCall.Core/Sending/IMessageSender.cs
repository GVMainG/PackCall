namespace PackCall.Core.Sending;

/// <summary>
/// Оператор отправки сообщений для одного канала.
/// </summary>
public interface IMessageSender
{
    /// <summary>
    /// Передаёт сообщение провайдеру и возвращает подтверждённый приём или отказ.
    /// При неопределённом исходе оператор выбрасывает исключение.
    /// Подтверждение приёма относится к провайдеру.
    /// </summary>
    /// <param name="request">Данные отправки.</param>
    /// <param name="ct">Токен отмены.</param>
    Task<SendResult> SendAsync(SendRequest request, CancellationToken ct);
}
