using System.Diagnostics;

namespace Knight.Api.BackgroundServices;

/// <summary>
/// Gives one background pass an identity, so everything it writes can be tied
/// back together afterwards.
///
/// A request gets its correlation id from the pipeline. Background work has no
/// request, and the audit entries the workers write used to land with the column
/// empty — the one column an operator uses to reconstruct what happened was
/// blank on exactly the entries nobody watched happen.
///
/// Deliberately not the trace id. Tracing is off unless a collector is
/// configured, so <see cref="Activity.Current"/> is null in most deployments and
/// in every developer's environment; hanging the audit trail's usability on
/// whether an exporter happens to be wired up would mean it works in the one
/// place it is least needed. When tracing <em>is</em> on, the pass still opens a
/// span, and both ids end up on the same log lines.
/// </summary>
internal static class BackgroundCorrelation
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? CorrelationId => Current.Value;

    /// <summary>
    /// Starts a pass: a fresh correlation id, and a span for it when anything is
    /// listening. Dispose ends both.
    /// </summary>
    public static IDisposable BeginPass(string name)
    {
        var previous = Current.Value;
        Current.Value = Guid.NewGuid().ToString();

        var activity = Composition.TelemetryComposition.ActivitySource.StartActivity(name);
        activity?.SetTag("knight.correlation_id", Current.Value);

        return new Pass(previous, activity);
    }

    private sealed class Pass : IDisposable
    {
        private readonly string? _previous;
        private readonly Activity? _activity;

        public Pass(string? previous, Activity? activity)
        {
            _previous = previous;
            _activity = activity;
        }

        public void Dispose()
        {
            _activity?.Dispose();
            Current.Value = _previous;
        }
    }
}
