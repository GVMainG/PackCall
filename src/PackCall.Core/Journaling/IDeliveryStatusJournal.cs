namespace PackCall.Core.Journaling;

/// <summary>Журнал смены статусов доставок.</summary>
public interface IDeliveryStatusJournal
{
    Task AppendAsync(StatusChangeEntry entry, CancellationToken ct);
}
