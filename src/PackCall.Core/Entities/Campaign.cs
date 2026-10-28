using PackCall.Core.Exceptions;

namespace PackCall.Core.Entities;

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

    public ICollection<Delivery> Deliveries { get; private set; } = new List<Delivery>();

    private Campaign() { }

    public static Campaign Create(string name, string messageText)
    {
        var now = DateTime.UtcNow;

        var result = new Campaign
        {
            Id = Guid.NewGuid(),
            Name = name,
            MessageText = messageText,
            Status = CampaignStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now
        };

        return result;
    }

    public void UpdateDetails(string name, string messageText, DateTime now)
    {
        if (Status != CampaignStatus.Draft)
            throw new InvalidOperationException("Редактировать можно только кампанию в статусе черновика.");

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageText);

        Name = name;
        MessageText = messageText;
        UpdatedAt = now;
    }

    #region Смена статусов

    /// <summary>Блокирует изменение и запуск кампании на время создания доставок.</summary>
    public void StatusFromDraftToForming()
    {
        EnsureTransition(CampaignStatus.Forming);
        Status = CampaignStatus.Forming;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Делает кампанию доступной после создания всех доставок.</summary>
    public void StatusFromFormingToDraft()
    {
        EnsureTransition(CampaignStatus.Draft);
        Status = CampaignStatus.Draft;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Переводит кампанию из черновика в состояние обработки и устанавливает даты начала и изменения.
    /// </summary>
    /// <remarks>
    /// Если кампания уже находится в состоянии обработки, метод не изменяет её состояние и даты.
    /// </remarks>
    /// <exception cref="InvalidStatusTransitionException">
    /// Кампания находится в состоянии, отличном от черновика или обработки.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Название или текст сообщения кампании равны <see langword="null" />.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Название или текст сообщения кампании пусты либо состоят только из пробельных символов.
    /// </exception>
    public void StatusFromDraftToInProgress()
    {
        if (Status == CampaignStatus.InProgress)
            return;
        if (Status != CampaignStatus.Draft)
            throw new InvalidStatusTransitionException(nameof(Campaign), Status, CampaignStatus.InProgress);
        EnsureTransition(CampaignStatus.InProgress);

        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(MessageText);

        Status = CampaignStatus.InProgress;
        var now = DateTime.UtcNow;
        StartedAt = now;
        UpdatedAt = now;
    }

    /// <summary>Приостанавливает запущенную кампанию; повторная пауза не меняет даты.</summary>
    public void StatusFromInProgressToPaused(DateTime now)
    {
        if (Status == CampaignStatus.Paused)
            return;
        EnsureTransition(CampaignStatus.Paused);
        Status = CampaignStatus.Paused;
        UpdatedAt = now;
    }

    /// <summary>Возобновляет кампанию, сохраняя дату первого запуска.</summary>
    public void StatusFromPausedToInProgress(DateTime now)
    {
        if (Status == CampaignStatus.InProgress)
            return;
        if (Status != CampaignStatus.Paused)
            throw new InvalidStatusTransitionException(nameof(Campaign), Status, CampaignStatus.InProgress);
        EnsureTransition(CampaignStatus.InProgress);
        Status = CampaignStatus.InProgress;
        UpdatedAt = now;
    }

    /// <summary>
    /// Переводит кампанию из статуса "Запущена" в статус "Завершена"
    /// и устанавливает даты завершения и изменения.
    /// </summary>
    /// <param name="now">Дата и время завершения в UTC.</param>
    /// <remarks>Повторный вызов для завершённой кампании не изменяет её состояние.</remarks>
    /// <exception cref="InvalidStatusTransitionException">Кампания находится в статусе, отличном от "Запущена".</exception>
    public void StatusFromInProgressToCompleted(DateTime now)
    {
        if (Status == CampaignStatus.Completed)
            return;
        EnsureTransition(CampaignStatus.Completed);

        Status = CampaignStatus.Completed;
        CompletedAt = now;
        UpdatedAt = now;
    }

    private void EnsureTransition(CampaignStatus target)
    {
        if (!CampaignStatusTransitions.CanTransition(Status, target))
            throw new InvalidStatusTransitionException(nameof(Campaign), Status, target);
    }

    #endregion Смена статусов
}
