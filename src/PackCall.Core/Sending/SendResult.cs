namespace PackCall.Core.Sending;

/// <summary>Результат вызова канала отправки.</summary>
/// <param name="IsSuccess">Сообщение принято оператором.</param>
/// <param name="Error">Причина ошибки оператора, если вызов завершился ошибкой.</param>
/// <param name="IsDuplicateSuppressed">
/// Оператор узнал ключ идемпотентности и подавил повторную фактическую отправку.
/// </param>
public sealed record SendResult(bool IsSuccess, string? Error = null, bool IsDuplicateSuppressed = false)
{
    public static SendResult Success(bool isDuplicateSuppressed = false) =>
        new(true, Error: null, isDuplicateSuppressed);

    public static SendResult Failure(string error) =>
        new(false, Error: error);
}
