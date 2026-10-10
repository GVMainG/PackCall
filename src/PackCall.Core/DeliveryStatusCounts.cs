namespace PackCall.Core;

/// <summary>Число доставок кампании по состояниям.</summary>
public sealed record DeliveryStatusCounts(int Total, int Waiting, int InProgress, int Sent, int Failed);
