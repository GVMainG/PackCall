using PackCall.Core.Exceptions;

namespace PackCall.Core.Entities;

/// <summary>
/// Доменная сущность: работа по отправке сообщения одному получателю
/// и правила смены её статуса.
/// </summary>
public sealed class Delivery
{
    public Guid Id { get; private set; }
    public Guid CampaignId { get; private set; }
    public Guid RecipientId { get; private set; }

    /// <summary>Снимок адреса получателя на момент создания доставки.</summary>
    public string RecipientEmail { get; private set; } = null!;

    public DeliveryStatus DeliveryStatus { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    /// <summary>Идентификатор воркера, захватившего доставку для обработки.</summary>
    public string? WorkerId { get; private set; }

    /// <summary>Срок, до которого доставка закреплена за воркером.</summary>
    public DateTime? LockExpiresAt { get; private set; }

    public Campaign Campaign { get; private set; } = null!;
    public Recipient Recipient { get; private set; } = null!;

    // Для восстановления сущности хранилищем.
    private Delivery() { }

    /// <summary>
    /// Создаёт доставку для одного получателя кампании со статусом "Ожидает".
    /// </summary>
    /// <param name="campaignId">Идентификатор кампании.</param>
    /// <param name="recipientId">Идентификатор получателя.</param>
    /// <param name="recipientEmail">Адрес получателя, сохраняемый как снимок.</param>
    /// <param name="now">Дата и время создания в UTC.</param>
    public static Delivery Create(Guid campaignId, Guid recipientId, string recipientEmail, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEmail);

        return new Delivery
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            RecipientId = recipientId,
            RecipientEmail = recipientEmail,
            DeliveryStatus = DeliveryStatus.Waiting,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// Переводит доставку из "Ожидает" в "Отправляется", закрепляя её за воркером
    /// на ограниченное время.
    /// </summary>
    /// <param name="workerId">Идентификатор воркера.</param>
    /// <param name="lockExpiresAt">Срок закрепления в UTC.</param>
    public void Capture(string workerId, DateTime lockExpiresAt)
    {
        EnsureTransition(DeliveryStatus.InProgress);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        DeliveryStatus = DeliveryStatus.InProgress;
        WorkerId = workerId;
        LockExpiresAt = lockExpiresAt;
    }

    /// <summary>Переводит доставку из "Отправляется" в конечный статус "Доставлена".</summary>
    public void MarkSent(DateTime now)
    {
        EnsureTransition(DeliveryStatus.Sent);
        Complete(now);
        DeliveryStatus = DeliveryStatus.Sent;
    }

    /// <summary>Переводит доставку из "Отправляется" в конечный статус "Ошибка".</summary>
    public void MarkFailed(DateTime now)
    {
        EnsureTransition(DeliveryStatus.Failed);
        Complete(now);
        DeliveryStatus = DeliveryStatus.Failed;
    }

    /// <summary>
    /// Возвращает доставку из "Отправляется" в "Ожидает", если срок закрепления истёк.
    /// </summary>
    /// <returns><see langword="true"/>, если доставка возвращена в "Ожидает".</returns>
    public bool TryReturnIfLockExpired(DateTime now)
    {
        if (DeliveryStatus != DeliveryStatus.InProgress)
            return false;
        if (LockExpiresAt is null || LockExpiresAt > now)
            return false;

        DeliveryStatus = DeliveryStatus.Waiting;
        WorkerId = null;
        LockExpiresAt = null;
        UpdatedAt = now;
        return true;
    }

    private void Complete(DateTime now)
    {
        CompletedAt = now;
        UpdatedAt = now;
        WorkerId = null;
        LockExpiresAt = null;
    }

    private void EnsureTransition(DeliveryStatus target)
    {
        if (!DeliveryStatusTransitions.CanTransition(DeliveryStatus, target))
            throw new InvalidStatusTransitionException(nameof(Delivery), DeliveryStatus, target);
    }
}
