namespace PackCall.Core.Diagnostics;

public interface IMeasuredFailure { Outcome MeasurementOutcome { get; } }

public static class MeasuredOperation
{
    public static Outcome Failure(Exception exception) => exception switch
    {
        OperationCanceledException => Outcome.Cancelled,
        IMeasuredFailure failure => failure.MeasurementOutcome,
        _ => Outcome.Error
    };

    public static async Task<T> Run<T>(Operation operation, Func<Task<T>> action,
        Func<T, Outcome>? outcome = null, Event? finished = null,
        Purpose purpose = Purpose.None, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var start = time.GetTimestamp();
        var result = Outcome.Error;
        try
        {
            var value = await action();
            result = outcome?.Invoke(value) ?? Outcome.Success;
            return value;
        }
        catch (Exception exception)
        {
            result = Failure(exception);
            throw;
        }
        finally
        {
            if (finished is { } metric) PerformanceMetrics.Count(metric, result, purpose: purpose);
            PerformanceMetrics.Duration(operation, result, time.GetElapsedTime(start).TotalMilliseconds, purpose);
        }
    }

    public static Task Run(Operation operation, Func<Task> action, Event? finished = null,
        Purpose purpose = Purpose.None, TimeProvider? time = null) =>
        Run(operation, async () => { await action(); return true; }, finished: finished, purpose: purpose, time: time);
}
