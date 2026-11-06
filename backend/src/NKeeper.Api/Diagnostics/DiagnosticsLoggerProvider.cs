using Microsoft.AspNetCore.Routing;

namespace NKeeper.Api.Diagnostics;

/// <summary>
/// Copies warnings and errors from the application log to the diagnostics file. It keeps a message's
/// template (<c>Couldn't record audit event {Action}</c>) and only values that can't hold a name or a
/// secret, like numbers; an exception's message is the one free text, and it's redacted.
/// </summary>
internal sealed class DiagnosticsLoggerProvider(DiagnosticsLog log, IHttpContextAccessor httpContext) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DiagnosticsLogger(categoryName, log, httpContext);

    public void Dispose()
    {
    }

    private sealed class DiagnosticsLogger(string category, DiagnosticsLog log, IHttpContextAccessor httpContext) : ILogger
    {
        private const int MaxStackLength = 8000;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel is >= LogLevel.Warning and < LogLevel.None
            && log.Enabled
            && !category.StartsWith("NKeeper.Api.Diagnostics", StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var fields = state as IEnumerable<KeyValuePair<string, object?>>;
            var template = fields?.FirstOrDefault(field => field.Key == "{OriginalFormat}").Value as string;
            var values = fields?.Where(field => field.Key != "{OriginalFormat}" && IsSafe(field.Value)).ToDictionary(field => field.Key, field => field.Value!.ToString());
            var context = httpContext.HttpContext;
            log.RecordError(
                category,
                logLevel.ToString(),
                template ?? log.Redactor.Redact(formatter(state, exception)),
                exception is null ? null : Describe(exception),
                exception is null ? null : Stack(exception),
                new
                {
                    values = values is { Count: > 0 } ? values : null,
                    trace = context?.TraceIdentifier,
                    route = (context?.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
                });
        }

        private static bool IsSafe(object? value) =>
            value is bool or byte or short or int or long or float or double or decimal or TimeSpan or Enum;

        private string Describe(Exception exception)
        {
            var parts = new List<string>();
            for (var current = exception; current is not null && parts.Count < 5; current = current.InnerException)
            {
                parts.Add($"{current.GetType().FullName}: {log.Redactor.Redact(current.Message)}");
            }

            return string.Join(" ---> ", parts);
        }

        /// <summary>Only the frames, which name the code: an exception's own text would bring its message back in.</summary>
        private static string? Stack(Exception exception)
        {
            var stacks = new List<string>();
            for (var current = exception; current is not null && stacks.Count < 5; current = current.InnerException)
            {
                if (current.StackTrace is { } stack)
                {
                    stacks.Add(stack.Trim());
                }
            }

            var all = string.Join("\n--- inner ---\n", Enumerable.Reverse(stacks));
            return all.Length == 0 ? null : all.Length > MaxStackLength ? all[..MaxStackLength] + "…" : all;
        }
    }
}
