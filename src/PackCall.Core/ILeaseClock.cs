namespace PackCall.Core;

/// <summary>Database time used only for the conservative lease budget before sending.</summary>
public interface ILeaseClock
{
    Task<DateTime> GetUtcNowAsync(CancellationToken ct);
}
