namespace PackCall.Core.Exceptions;

/// <summary>Получатель с таким email уже есть в аудитории.</summary>
public sealed class RecipientAlreadyExistsException : DomainException
{
    public RecipientAlreadyExistsException()
        : base("Получатель с таким email уже существует.")
    {
    }

    public RecipientAlreadyExistsException(Exception innerException)
        : base("Получатель с таким email уже существует.", innerException)
    {
    }
}
