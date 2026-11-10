using PackCall.Core.Diagnostics;

namespace PackCall.Infrastructure.Sending;

public sealed class TestOperatorValidationException() : Exception("TestOperatorValidation"), IMeasuredFailure
{
    public Outcome MeasurementOutcome => Outcome.Validation;
}
public sealed class TestOperatorProtocolException() : Exception("TestOperatorProtocol"), IMeasuredFailure
{
    public Outcome MeasurementOutcome => Outcome.Protocol;
}
public sealed class TestOperatorServiceException() : Exception("TestOperatorService"), IMeasuredFailure
{
    public Outcome MeasurementOutcome => Outcome.Service;
}
public sealed class TestOperatorTransportException() : Exception("TestOperatorTransport"), IMeasuredFailure
{
    public Outcome MeasurementOutcome => Outcome.Transport;
}
public sealed class TestOperatorTimeoutException() : Exception("TestOperatorTimeout"), IMeasuredFailure
{
    public Outcome MeasurementOutcome => Outcome.Timeout;
}
