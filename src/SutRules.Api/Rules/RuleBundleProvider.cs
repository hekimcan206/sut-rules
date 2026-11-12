using System.Globalization;
using System.Text;
using SutRules.Api.Infrastructure;
using SutRules.Engine;
using SutRules.Language;

namespace SutRules.Api.Rules;

public sealed class RulesOptions
{
    public const string Section = "Rules";

    /// <summary>Directory with *.rule files and catalog/sut-catalog.json.</summary>
    public string Directory { get; set; } = "rules";

    /// <summary>How often the directory is checked for changes; zero disables reloading.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public int MaxInvoicesPerRequest { get; set; } = 5000;
}

/// <summary>The outcome of the last reload attempt that failed; the previous bundle keeps serving.</summary>
public sealed record ReloadFailure(DateTimeOffset At, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Holds the active bundle and swaps it atomically when the rule directory changes. A new bundle replaces the old
/// one only if every file compiles; otherwise the old bundle keeps serving and the failure is exposed on
/// <c>/api/v1/ruleset</c> and in the readiness check. In-flight requests finish on the bundle they started with,
/// because they hold their own reference to it.
/// </summary>
/// <remarks>
/// The directory is polled with a cheap fingerprint (names, sizes, modification times) instead of using
/// FileSystemWatcher: change notifications do not cross Docker Desktop bind mounts reliably, and ConfigMap updates in
/// Kubernetes arrive as symlink swaps that watchers report inconsistently.
/// </remarks>
public sealed class RuleBundleProvider : BackgroundService
{
    private readonly RulesOptions _options;
    private readonly RulesMetrics _metrics;
    private readonly ILogger<RuleBundleProvider> _logger;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private Active _active;
    private string _fingerprint;

    public RuleBundleProvider(Microsoft.Extensions.Options.IOptions<RulesOptions> options, RulesMetrics metrics, ILogger<RuleBundleProvider> logger, TimeProvider clock)
    {
        _options = options.Value;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;

        // A broken bundle at startup is fatal: an engine that starts without its rules would pass invoices it should
        // reject. The exception surfaces in the host log and the container exits.
        _fingerprint = Fingerprint();
        var bundle = RuleBundle.Load(_options.Directory, clock);
        _active = new Active(bundle, new RuleEngine(bundle), 1);
        _metrics.Reloaded(success: true);
        LogLoaded(_logger, bundle.Rules.Count, bundle.Hash, _options.Directory, null);
    }

    public RuleEngine Engine => Volatile.Read(ref _active).Engine;

    public RuleBundle Bundle => Volatile.Read(ref _active).Bundle;

    /// <summary>Incremented on every successful swap.</summary>
    public int Generation => Volatile.Read(ref _active).Generation;

    public ReloadFailure? LastFailure { get; private set; }

    /// <summary>Reloads when the directory changed. Returns true when a new bundle became active.</summary>
    public bool ReloadIfChanged()
    {
        lock (_gate)
        {
            var fingerprint = Fingerprint();
            if (fingerprint == _fingerprint)
            {
                return false;
            }

            _fingerprint = fingerprint;
            try
            {
                var bundle = RuleBundle.Load(_options.Directory, _clock);
                if (bundle.Hash == Bundle.Hash)
                {
                    LastFailure = null;
                    return false; // touched, not changed
                }

                var next = new Active(bundle, new RuleEngine(bundle), Generation + 1);
                Volatile.Write(ref _active, next);
                LastFailure = null;
                _metrics.Reloaded(success: true);
                LogLoaded(_logger, bundle.Rules.Count, bundle.Hash, _options.Directory, null);
                return true;
            }
            catch (RuleCompilationException ex)
            {
                LastFailure = new ReloadFailure(_clock.GetUtcNow(), ex.Diagnostics);
                _metrics.Reloaded(success: false);
                LogRejected(_logger, ex.Diagnostics.Count, Bundle.Hash, string.Join(" | ", ex.Diagnostics.Take(5)), null);
                return false;
            }
            catch (IOException ex)
            {
                // A file caught mid-write: forget the fingerprint so the next poll tries again.
                _fingerprint = string.Empty;
                LogIoRetry(_logger, ex.Message, null);
                return false;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.PollInterval <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.PollInterval, _clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            ReloadIfChanged();
        }
    }

    private string Fingerprint()
    {
        var dir = new DirectoryInfo(_options.Directory);
        if (!dir.Exists)
        {
            return "missing";
        }

        var catalog = new DirectoryInfo(Path.Combine(dir.FullName, "catalog"));
        var files = dir.EnumerateFiles("*.rule").Concat(catalog.Exists ? catalog.EnumerateFiles("*.json") : []);
        var sb = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.FullName, StringComparer.Ordinal))
        {
            sb.Append(file.FullName).Append('|').Append(file.Length.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return sb.ToString();
    }

    private sealed record Active(RuleBundle Bundle, RuleEngine Engine, int Generation);

    private static readonly Action<ILogger, int, string, string, Exception?> LogLoaded =
        LoggerMessage.Define<int, string, string>(LogLevel.Information, new EventId(1, "BundleLoaded"),
            "Loaded {RuleCount} rules, bundle {BundleHash}, from {Directory}");

    private static readonly Action<ILogger, int, string, string, Exception?> LogRejected =
        LoggerMessage.Define<int, string, string>(LogLevel.Error, new EventId(2, "BundleRejected"),
            "Rule reload rejected with {ErrorCount} error(s); still serving bundle {BundleHash}. {Diagnostics}");

    private static readonly Action<ILogger, string, Exception?> LogIoRetry =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "BundleReadRetry"), "Rule files could not be read ({Reason}); retrying on the next poll");
}
