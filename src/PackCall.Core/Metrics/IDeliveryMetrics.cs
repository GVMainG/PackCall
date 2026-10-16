using PackCall.Core.Entities;

namespace PackCall.Core.Metrics;

/// <summary>
/// Метрики обработки доставок для наблюдаемости нагрузочного прогона.
/// </summary>
public interface IDeliveryMetrics
{
    /// <summary>Увеличивает накопительный счётчик захваченных доставок.</summary>
    void RecordCaptured(int count);

    /// <summary>Фиксирует одну обработанную доставку (скорость обработки).</summary>
    void RecordProcessed();

    /// <summary>Фиксирует переход доставки между статусами.</summary>
    void RecordStatusChange(DeliveryStatus previous, DeliveryStatus current);

    /// <summary>Фиксирует число доставок, возвращённых в "Ожидает" из-за истёкшего закрепления.</summary>
    void RecordExpiredLockReturned(int count);

    /// <summary>Фиксирует повторный вызов mock-оператора с тем же ключом идемпотентности.</summary>
    void RecordOperatorRetry();

    /// <summary>Фиксирует подавленный оператором дубль фактической отправки.</summary>
    void RecordOperatorDuplicateSuppressed();

    /// <summary>Фиксирует успешный вызов mock-оператора.</summary>
    void RecordOperatorSuccess();

    /// <summary>Фиксирует ошибочный вызов mock-оператора.</summary>
    void RecordOperatorError();
}
