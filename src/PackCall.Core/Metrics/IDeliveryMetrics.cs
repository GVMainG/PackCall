using PackCall.Core.Models;

namespace PackCall.Core.Metrics;

/// <summary>Метрики обработки доставок для наблюдаемости нагрузочного прогона.</summary>
public interface IDeliveryMetrics
{
    /// <summary>Фиксирует число захваченных доставок (активные закрепления).</summary>
    void RecordCaptured(int count);

    /// <summary>Фиксирует одну обработанную доставку (скорость обработки).</summary>
    void RecordProcessed();

    /// <summary>Фиксирует смену статуса доставки (счётчики по статусам).</summary>
    void RecordStatusChange(DeliveryStatus previous, DeliveryStatus current);

    /// <summary>Фиксирует число доставок, возвращённых в «Ожидает» из-за истёкшего закрепления.</summary>
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
