namespace PackCall.Core.Metrics;

/// <summary>Читает текущие показатели доставок из хранилища.</summary>
public interface IDeliveryStateSnapshotReader
{
    Task<DeliveryStateSnapshot> ReadAsync(CancellationToken ct);
}
