namespace PackCall.Core.Exceptions;

public sealed class CampaignFormationException(Exception cause)
    : Exception("Не удалось сформировать кампанию.", cause);
