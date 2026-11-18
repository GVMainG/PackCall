namespace PackCall.Core.Exceptions;

public sealed class ContactWriteDeadlockException(Exception innerException)
    : Exception("Запись контакта прервана. Повторите запрос.", innerException);
