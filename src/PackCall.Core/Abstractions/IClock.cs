namespace PackCall.Core.Abstractions;

/// <summary>
/// Абстракция текущего времени для детерминированных сценариев.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
