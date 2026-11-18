namespace PackCall.Core.Exceptions;

public sealed class ChannelOperatorUnavailableException(string channel)
    : DomainException($"Для канала «{channel}» отсутствует оператор отправки.");
