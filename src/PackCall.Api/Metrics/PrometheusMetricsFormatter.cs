using System.Globalization;
using System.Text;
using PackCall.Core.Metrics;
using PackCall.Infrastructure.Metrics;

namespace PackCall.Api.Metrics;

internal static class PrometheusMetricsFormatter
{
    public static string Format(DeliveryMetricTotals totals, DeliveryStateSnapshot? state)
    {
        var text = new StringBuilder(1200);

        Write(text, "packcall_deliveries_captured_total", "counter",
            "Deliveries captured by this API instance.", totals.Captured);
        Write(text, "packcall_deliveries_processed_total", "counter",
            "Deliveries processed by this API instance; use rate() for deliveries per second.", totals.Processed);
        Write(text, "packcall_deliveries_expired_locks_returned_total", "counter",
            "Expired delivery locks returned to Waiting by this API instance.", totals.ExpiredLocksReturned);
        text.Append("# HELP packcall_mock_email_calls_total Mock-email calls by result on this API instance.\n");
        text.Append("# TYPE packcall_mock_email_calls_total counter\n");
        WriteValue(text, "packcall_mock_email_calls_total{result=\"success\"}", totals.OperatorSuccess);
        WriteValue(text, "packcall_mock_email_calls_total{result=\"error\"}", totals.OperatorError);

        Write(text, "packcall_storage_up", "gauge", "PostgreSQL metrics query succeeded on this scrape.",
            state is null ? 0 : 1);

        if (state is not null)
        {
            text.Append("# HELP packcall_deliveries Current deliveries in PostgreSQL by status.\n");
            text.Append("# TYPE packcall_deliveries gauge\n");
            WriteValue(text, "packcall_deliveries{status=\"waiting\"}", state.Waiting);
            WriteValue(text, "packcall_deliveries{status=\"in_progress\"}", state.InProgress);
            WriteValue(text, "packcall_deliveries{status=\"sent\"}", state.Sent);
            WriteValue(text, "packcall_deliveries{status=\"failed\"}", state.Failed);

            Write(text, "packcall_deliveries_active_locks", "gauge",
                "Deliveries with unexpired worker locks in PostgreSQL.", state.ActiveLocks);
            Write(text, "packcall_deliveries_expired_locks", "gauge",
                "Deliveries with expired worker locks awaiting recovery in PostgreSQL.", state.ExpiredLocks);
        }

        return text.ToString();
    }

    private static void Write(StringBuilder text, string name, string type, string help, long value)
    {
        text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        text.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        WriteValue(text, name, value);
    }

    private static void WriteValue(StringBuilder text, string name, long value) =>
        text.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
}
