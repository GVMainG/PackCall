namespace PackCall.Infrastructure.Sending;

public sealed class TestOperatorValidationException() : Exception("TestOperatorValidation");
public sealed class TestOperatorProtocolException() : Exception("TestOperatorProtocol");
public sealed class TestOperatorServiceException() : Exception("TestOperatorService");
public sealed class TestOperatorTransportException() : Exception("TestOperatorTransport");
public sealed class TestOperatorTimeoutException() : Exception("TestOperatorTimeout");
