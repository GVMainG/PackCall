using PackCall.Core;
using PackCall.Core.Entities;

namespace PackCall.Api.Models;

internal static class ResponseMapping
{
    public static CampaignResponse ToResponse(this Campaign campaign) => new(
        campaign.Id, campaign.Name, campaign.MessageText, campaign.Status,
        ToOffset(campaign.CreatedAt), ToOffset(campaign.UpdatedAt),
        ToOffset(campaign.StartedAt), ToOffset(campaign.CompletedAt));

    public static DeliveryResponse ToResponse(this Delivery delivery) => new(
        delivery.Id, delivery.CampaignId, delivery.RecipientId, delivery.RecipientEmail,
        delivery.DeliveryStatus, ToOffset(delivery.CreatedAt), ToOffset(delivery.UpdatedAt),
        ToOffset(delivery.CompletedAt), delivery.WorkerId, ToOffset(delivery.LockExpiresAt));

    public static RecipientResponse ToResponse(this Recipient recipient) => new(
        recipient.Id, recipient.Email, ToOffset(recipient.CreatedAt), ToOffset(recipient.UpdatedAt));

    public static DeliveryStatisticsResponse ToResponse(this DeliveryStatusCounts counts) => new(
        counts.Total, counts.Waiting, counts.InProgress, counts.Sent, counts.Failed);

    public static PageResponse<TResponse> ToResponse<TEntity, TResponse>(
        this PageResult<TEntity> page, int pageNumber, int pageSize, Func<TEntity, TResponse> map) =>
        new(page.Items.Select(map).ToList(), pageNumber, pageSize, page.TotalCount,
            page.TotalCount / pageSize + (page.TotalCount % pageSize == 0 ? 0 : 1));

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value.HasValue ? ToOffset(value.Value) : null;
}
