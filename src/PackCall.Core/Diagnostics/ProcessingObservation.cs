namespace PackCall.Core.Diagnostics;

/// <summary>Attempt-local outcome propagation without changing ProcessAsync's public contract.</summary>
public sealed class ProcessingObservation : IDisposable
{
    private static readonly AsyncLocal<ProcessingObservation?> Slot = new();
    private readonly ProcessingObservation? _previous;
    public static ProcessingObservation? Current => Slot.Value;
    public Outcome Outcome { get; set; } = Outcome.Error;
    public ProcessingObservation() { _previous = Slot.Value; Slot.Value = this; }
    public void Dispose() => Slot.Value = _previous;
}
