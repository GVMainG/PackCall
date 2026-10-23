namespace PackCall.Api.Background;

/// <summary>Настройки фоновой обработки доставок.</summary>
public sealed class DeliveryWorkerOptions
{
    public const string SectionName = "DeliveryWorker";

    /// <summary>Количество параллельных обработчиков.</summary>
    public int Parallelism { get; set; } = 4;
    /// <summary>Максимальное число доставок в пакете одного обработчика; обрабатываются по очереди.</summary>
    public int BatchSize { get; set; } = 10;
    /// <summary>Срок закрепления всего пакета, включая ожидание обработки.</summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsValid() =>
        Parallelism is >= 1 and <= 64 &&
        BatchSize >= 1 &&
        LockDuration >= TimeSpan.FromSeconds(10) &&
        IdleDelay > TimeSpan.Zero &&
        RecoveryInterval > TimeSpan.Zero &&
        ErrorDelay > TimeSpan.Zero &&
        RecoveryInterval < TimeSpan.FromMinutes(2) &&
        LockDuration <= TimeSpan.FromMinutes(2) - RecoveryInterval;
}
