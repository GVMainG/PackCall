namespace PackCall.Infrastructure.Sending;

public sealed class MockEmailOptions
{
    /// <summary>Искусственная задержка ответа mock-оператора.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Адреса, для которых mock-оператор возвращает явную ошибку.</summary>
    public ISet<string> FailedRecipients { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Текст явной ошибки mock-оператора.</summary>
    public string FailureMessage { get; set; } = "Mock-email отклонил отправку.";
}
