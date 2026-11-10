namespace PackCall.Core.Diagnostics;

/// <summary>Explicit opt-in. No configuration/environment dump is ever exported.</summary>
public sealed record MeasurementOptions
{
    public bool Enabled { get; init; }
    public Guid SessionId { get; init; }
    public string OutputDirectory { get; init; } = "";
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (Enabled && (SessionId == Guid.Empty || string.IsNullOrWhiteSpace(OutputDirectory) || Interval <= TimeSpan.Zero))
            throw new ArgumentException("Invalid measurement settings.");
    }
}

public sealed record EffectiveMeasurementSettings(
    int Parallelism, int BatchSize, double LockDurationSeconds,
    double RecoveryIntervalSeconds, double IdleDelaySeconds, double ErrorDelaySeconds,
    double HttpTimeoutSeconds, bool SimulateErrors, string OperatorMode);
