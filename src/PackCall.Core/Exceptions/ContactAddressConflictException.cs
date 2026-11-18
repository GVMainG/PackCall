namespace PackCall.Core.Exceptions;

public sealed class ContactAddressConflictException : DomainException
{
    private const string Description = "Этот адрес канала уже принадлежит другому контакту.";

    public ContactAddressConflictException() : base(Description) { }

    public ContactAddressConflictException(Exception innerException) : base(Description, innerException) { }
}
