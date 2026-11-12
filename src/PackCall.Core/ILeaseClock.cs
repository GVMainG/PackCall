namespace PackCall.Core;

/// <summary>Время БД используется только для расчёта оставшегося срока закрепления с запасом перед отправкой.</summary>
public interface ILeaseClock
{
    Task<DateTime> GetUtcNowAsync(CancellationToken ct);
}
