namespace PackCall.Core.Sending;

/// <summary>Результат вызова канала отправки.</summary>
/// <param name="IsSuccess">Провайдер подтвердил приём сообщения; доставка адресату неизвестна.</param>
/// <param name="Error">Причина ошибки оператора, если вызов завершился ошибкой.</param>
public sealed record SendResult(bool IsSuccess, string? Error = null)
{
    public static SendResult Success() => new(true);

    public static SendResult Failure(string error) =>
        new(false, Error: error);
}
