namespace PackCall.Core.Exceptions;

public sealed class CampaignContactsNotFoundException()
    : ArgumentException(Reason, "contactIds")
{
    public const string Reason = "Один или несколько выбранных контактов не найдены.";
}
