using System.Collections.ObjectModel;

namespace PackCall.Core.Entities;

public enum CampaignStatus
{
    Draft = 0,
    InProgress = 1,
    Completed = 2,
    Forming = 3,
    Paused = 4,
    Failed = -1
}

/// <summary>Допустимые переходы статусов кампании.</summary>
public static class CampaignStatusTransitions
{
    private static readonly ReadOnlySet<CampaignStatus> DraftTargets = [CampaignStatus.Forming, CampaignStatus.InProgress];
    private static readonly ReadOnlySet<CampaignStatus> FormingTargets = [CampaignStatus.Draft];
    private static readonly ReadOnlySet<CampaignStatus> InProgressTargets = [CampaignStatus.Completed, CampaignStatus.Paused];
    private static readonly ReadOnlySet<CampaignStatus> PausedTargets = [CampaignStatus.InProgress];

    public static bool CanTransition(CampaignStatus from, CampaignStatus to) =>
        GetAllowedTargets(from).Contains(to);

    public static IReadOnlySet<CampaignStatus> GetAllowedTargets(CampaignStatus from) => from switch
    {
        CampaignStatus.Draft => DraftTargets,
        CampaignStatus.Forming => FormingTargets,
        CampaignStatus.InProgress => InProgressTargets,
        CampaignStatus.Paused => PausedTargets,
        _ => ReadOnlySet<CampaignStatus>.Empty
    };
}
