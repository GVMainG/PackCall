using PackCall.Core.Entities;

namespace PackCall.Core.Exceptions;

/// <summary>Попытка выполнить недопустимый переход статуса.</summary>
public sealed class InvalidStatusTransitionException : DomainException
{
    public InvalidStatusTransitionException(string entityName, object previousStatus, object targetStatus)
        : base(BuildMessage(entityName, previousStatus, targetStatus))
    {
        EntityName = entityName;
        PreviousStatus = previousStatus;
        TargetStatus = targetStatus;
    }

    public string EntityName { get; }

    public object PreviousStatus { get; }

    public object TargetStatus { get; }

    private static string BuildMessage(string entityName, object previousStatus, object targetStatus)
    {
        var allowed = previousStatus is DeliveryStatus from
            ? string.Join(", ", DeliveryStatusTransitions.GetAllowedTargets(from))
            : string.Empty;

        var suffix = allowed.Length == 0
            ? "переходы из этого статуса запрещены"
            : $"допустимые переходы: {allowed}";

        return $"Недопустимый переход статуса сущности {entityName} из \"{previousStatus}\" в \"{targetStatus}\": {suffix}.";
    }
}
