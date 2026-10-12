namespace PackCall.Api.Background;

/// <summary>Настройки фоновой обработки доставок.</summary>
public sealed class DeliveryWorkerOptions
{
    public const string SectionName = "DeliveryWorker";

    public int Parallelism { get; set; } = 4;
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsValid() =>
        Parallelism is >= 1 and <= 64 &&
        LockDuration >= TimeSpan.FromSeconds(10) &&
        IdleDelay > TimeSpan.Zero &&
        RecoveryInterval > TimeSpan.Zero &&
        ErrorDelay > TimeSpan.Zero &&
        RecoveryInterval < TimeSpan.FromMinutes(2) &&
        LockDuration <= TimeSpan.FromMinutes(2) - RecoveryInterval;
}
