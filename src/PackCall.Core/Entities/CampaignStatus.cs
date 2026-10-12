namespace PackCall.Core.Entities;

public enum CampaignStatus
{
    Draft = 0,
    InProgress = 1,
    Completed = 2,
    Failed = -1
}

/// <summary>Допустимые переходы статусов кампании.</summary>
public static class CampaignStatusTransitions
{
    private static readonly IReadOnlyDictionary<CampaignStatus, IReadOnlySet<CampaignStatus>> Allowed =
        new Dictionary<CampaignStatus, IReadOnlySet<CampaignStatus>>
        {
            [CampaignStatus.Draft] = new HashSet<CampaignStatus> { CampaignStatus.InProgress },
            [CampaignStatus.InProgress] = new HashSet<CampaignStatus> { CampaignStatus.Completed },
            [CampaignStatus.Completed] = new HashSet<CampaignStatus>(),
            [CampaignStatus.Failed] = new HashSet<CampaignStatus>()
        };

    public static bool CanTransition(CampaignStatus from, CampaignStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlySet<CampaignStatus> GetAllowedTargets(CampaignStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : new HashSet<CampaignStatus>();

    public static bool IsTerminal(CampaignStatus status) =>
        status is CampaignStatus.Completed or CampaignStatus.Failed;
}
