using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry;
using Sentry.Extensibility;
using Sentry.Extensions.Logging;

namespace Nexora.Worker.Observability;

public static partial class SentryObservability
{
    private const string SafeExceptionMessage = "Unhandled worker exception";

    public static void Configure(
        SentryLoggingOptions options,
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        options.Dsn = ReadDsn(configuration);
        options.Environment = environment.EnvironmentName;
        options.Release = ReadRelease(configuration);
        options.SendDefaultPii = false;
        options.MinimumBreadcrumbLevel = LogLevel.None;
        options.MinimumEventLevel = LogLevel.None;
        options.InitializeSdk = true;
        options.EnableLogs = false;
        options.CaptureFailedRequests = false;
        options.DisableSentryHttpMessageHandler = true;
        options.TracesSampleRate = null;
        options.ProfilesSampleRate = null;
        options.SetBeforeSendMetric(static _ => null!);
        options.SetBeforeSend(static (sentryEvent, _) => Sanitize(sentryEvent));
    }

    public static SentryEvent Sanitize(SentryEvent sentryEvent)
    {
        if (sentryEvent.Request is { } request)
        {
            sentryEvent.Request = new SentryRequest
            {
                Method = request.Method,
                Url = SafePath(request.Url)
            };
        }

        sentryEvent.User = null!;
        sentryEvent.Message = new SentryMessage { Message = SafeExceptionMessage };

        if (sentryEvent.SentryExceptions is not null)
        {
            foreach (var exception in sentryEvent.SentryExceptions)
                exception.Value = SafeExceptionMessage;
        }

        foreach (var key in sentryEvent.Tags.Keys
                     .Where(key => IsSensitive(key) || IsSensitive(sentryEvent.Tags[key]))
                     .ToArray())
            sentryEvent.UnsetTag(key);

        if (sentryEvent.Extra is IDictionary extra)
            extra.Clear();

        if (sentryEvent.Breadcrumbs is ICollection<Breadcrumb> breadcrumbs)
            breadcrumbs.Clear();

        return sentryEvent;
    }

    public static string? ReadRelease(IConfiguration configuration)
    {
        var release = configuration["Sentry:Release"]?.Trim();
        return string.IsNullOrWhiteSpace(release) ? null : release[..Math.Min(release.Length, 200)];
    }

    private static string? ReadDsn(IConfiguration configuration)
    {
        var dsn = configuration["Sentry:Dsn"]?.Trim();
        return string.IsNullOrWhiteSpace(dsn) ? string.Empty : dsn;
    }

    private static string SafePath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "/";

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return LimitPath(absolute.AbsolutePath);

        var queryStart = url.IndexOfAny(['?', '#']);
        return LimitPath(queryStart < 0 ? url : url[..queryStart]);
    }

    private static string LimitPath(string path)
    {
        var safe = string.IsNullOrWhiteSpace(path) ? "/" : path;
        safe = ControlCharacters().Replace(safe, string.Empty);
        return safe.Length <= 512 ? safe : safe[..512];
    }

    private static bool IsSensitive(string? value) => !string.IsNullOrWhiteSpace(value) &&
        SensitiveTerms().IsMatch(value);

    [GeneratedRegex("authorization|bearer|cookie|set-cookie|access[_-]?token|refresh[_-]?token|api[_-]?key|secret|password|cv|resume|answer|transcript|prompt|response|provider|payment|gemini|deepseek|resend|r2", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveTerms();

    [GeneratedRegex("[\\p{Cc}\\p{Cf}]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlCharacters();
}

public interface IWorkerSentryReporter
{
    void Capture(Exception exception, string executionId);
}

public sealed class WorkerSentryReporter(IHub hub) : IWorkerSentryReporter
{
    public void Capture(Exception exception, string executionId)
    {
        if (!hub.IsEnabled)
            return;

        hub.CaptureException(exception, scope =>
        {
            scope.SetTag("service", "worker");
            scope.SetTag("worker_cycle_id", executionId);
        });
    }
}
