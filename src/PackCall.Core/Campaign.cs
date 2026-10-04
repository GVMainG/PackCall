using PackCall.Core.Models;

namespace PackCall.Core;

/// <summary>
/// Доменная сущность: состояние одной кампании и правила его изменения.
/// </summary>
public sealed class Campaign
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string MessageText { get; private set; } = null!;
    public CampaignStatus Status { get; private set; }
    public DateTime? CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    public ICollection<DeliveryModel> Deliveries { get; private set; } = new List<DeliveryModel>();

    // Для восстановления сущности хранилищем.
    private Campaign() { }

    public static Campaign Create(string name, string messageText, DateTime now)
    {
        // TODO: проверить данные и создать кампанию в начальном состоянии.
        throw new NotImplementedException();
    }

    public void UpdateDetails(string name, string messageText, DateTime now)
    {
        // TODO: проверить допустимость редактирования и изменить данные.
        throw new NotImplementedException();
    }

    public void Start(DateTime now)
    {
        // TODO: проверить допустимость запуска и изменить состояние кампании.
        throw new NotImplementedException();
    }
}

public enum CampaignStatus
{
    Draft = 0,
    InProgress = 1,
    Completed = 2,
    Failed = -1
}
