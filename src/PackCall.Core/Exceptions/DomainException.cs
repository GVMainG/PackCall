namespace PackCall.Core.Exceptions;

/// <summary>Базовое исключение бизнес-правил предметной области.</summary>
public class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
