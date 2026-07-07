using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Configures Serilog file logging for post-mortem debugging of catastrophic crashes
///     that can't reach the MCP client. Logs to <c>%LOCALAPPDATA%/Zphil.Roz/logs/</c>.
/// </summary>
internal static class SerilogConfiguration
{
    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SessionId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    ///     Absolute path to the daily-rolling log directory. Exposed for the setup command
    ///     and ARCHITECTURE.md so users can locate post-mortem logs.
    /// </summary>
    internal static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zphil.Roz",
        "logs");

    /// <summary>
    ///     Creates the static <see cref="Log.Logger" /> with a daily rolling file sink.
    /// </summary>
    /// <remarks>
    ///     Call before any host building so crash handlers can use it immediately.
    /// </remarks>
    public static void InitializeFileLogger()
    {
        LogEventLevel minimumLevel = ParseLogLevel();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.WithProperty("SessionId", SessionContext.Current)
            .WriteTo.File(
                Path.Combine(LogDirectory, "roz-mcp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                // Concurrent server processes write to the same daily file (a shared mutex
                // serialises writes); the [{SessionId}] field then disambiguates interleaved
                // lines. Without this they would roll to _NNN siblings and the SessionId
                // tagging documented for cross-process correlation would never apply.
                shared: true,
                outputTemplate: OutputTemplate)
            .CreateLogger();
    }

    /// <summary>
    ///     Registers process-level crash handlers that log fatal errors and flush before exit.
    /// </summary>
    public static void RegisterCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled exception");
            }

            Log.CloseAndFlush();
        };

        // Non-fatal: log but don't close the logger — the process continues running
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
        };
    }

    /// <summary>
    ///     Adds Serilog and console (stderr) logging to the host builder.
    /// </summary>
    /// <remarks>
    ///     Console goes to stderr because stdout is reserved for MCP JSON-RPC protocol.
    /// </remarks>
    public static void AddSerilogLogging(this HostApplicationBuilder builder)
    {
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSerilog();
    }

    private static LogEventLevel ParseLogLevel() =>
        ParseLogLevel(Environment.GetEnvironmentVariable(RozEnvVars.LogLevel.Name));

    /// <summary>
    ///     Parses a <c>ROZ_LOG_LEVEL</c> value into a Serilog level, accepting both
    ///     <see cref="LogLevel" /> and <see cref="LogEventLevel" /> names and falling back to
    ///     <see cref="LogEventLevel.Warning" /> for null, blank, or unrecognised input.
    /// </summary>
    internal static LogEventLevel ParseLogLevel(string? envValue)
    {
        if (envValue is null)
        {
            return LogEventLevel.Warning;
        }

        // Accept Microsoft.Extensions.Logging.LogLevel names for backwards compatibility.
        // Enum.TryParse also binds numeric strings ("99") to an undefined enum value, so
        // guard with Enum.IsDefined to keep them on the Warning fallback.
        if (Enum.TryParse(envValue, true, out LogLevel msLevel) && Enum.IsDefined(msLevel))
        {
            return LevelConvert.ToSerilogLevel(msLevel);
        }

        // Also accept Serilog level names directly
        if (Enum.TryParse(envValue, true, out LogEventLevel serilogLevel) && Enum.IsDefined(serilogLevel))
        {
            return serilogLevel;
        }

        return LogEventLevel.Warning;
    }
}
