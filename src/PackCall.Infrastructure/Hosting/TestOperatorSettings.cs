using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace PackCall.Infrastructure.Hosting;

public enum TestSenderMode { Inprocess, Http }

public sealed class PackCallConfigurationException(string message) : Exception(message);

public sealed record TestOperatorSettings(TestSenderMode Mode, bool SimulateErrors, Uri? OperatorUrl = null, TimeSpan? Timeout = null)
{
    public static TestOperatorSettings Parse(IConfiguration configuration)
    {
        static PackCallConfigurationException Invalid(string key) => new($"{key}: invalid or missing configuration.");
        var environment = configuration["PACKCALL_ENVIRONMENT"]?.Trim().ToLowerInvariant();
        if (environment == "prod")
            throw new PackCallConfigurationException("PACKCALL_ENVIRONMENT=prod is not supported");
        if (environment != "test") throw Invalid("PACKCALL_ENVIRONMENT");
        var mode = configuration["TEST_SENDER_MODE"]?.Trim().ToLowerInvariant() switch
        {
            "inprocess" => TestSenderMode.Inprocess,
            "http" => TestSenderMode.Http,
            _ => throw Invalid("TEST_SENDER_MODE")
        };
        var rawBool = configuration["TEST_SIMULATE_ERRORS"];
        var simulate = false;
        if (rawBool is not null && !bool.TryParse(rawBool.Trim(), out simulate)) throw Invalid("TEST_SIMULATE_ERRORS");
        Uri? url = null;
        var seconds = 5d;
        if (mode == TestSenderMode.Http)
        {
            if (!Uri.TryCreate(configuration["TEST_OPERATOR_URL"], UriKind.Absolute, out url)
                || (url.Scheme != "http" && url.Scheme != "https") || string.IsNullOrEmpty(url.Host)
                || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || url.AbsolutePath != "/")
                throw Invalid("TEST_OPERATOR_URL");
            var timeout = configuration["TEST_OPERATOR_TIMEOUT_SECONDS"];
            if (timeout is not null && (!double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                || !double.IsFinite(seconds) || seconds <= 0 || seconds > 30)) throw Invalid("TEST_OPERATOR_TIMEOUT_SECONDS");
        }
        return new(mode, simulate, url, // CancelAfter uses whole milliseconds. Round up instead of truncating a valid positive deadline to zero.
            TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(seconds * 1000))));
    }
}
