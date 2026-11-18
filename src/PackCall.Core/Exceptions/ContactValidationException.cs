namespace PackCall.Core.Exceptions;

public sealed class ContactValidationException(string message) : DomainException(message);
