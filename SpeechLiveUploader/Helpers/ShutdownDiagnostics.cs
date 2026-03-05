using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Hosting;
using SpeechLiveUploader.Services;

/// <summary>
/// Provides comprehensive shutdown diagnostics to help identify why the service stopped.
/// Captures unhandled exceptions, process exit events, and shutdown reasons.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShutdownDiagnostics
{
    private static readonly string ShutdownLogPath = Path.Combine(ManageFiles.LogsFolderPath, "shutdown_events.log");
    private static readonly object _logLock = new object();
    private static bool _isInitialized = false;
    private static string _lastShutdownReason = "Unknown";
    private static DateTime _startTime;

    /// <summary>
    /// Initializes all shutdown diagnostic handlers. Call this early in Program.cs.
    /// </summary>
    public static void Initialize()
    {
        if (_isInitialized) return;
        _isInitialized = true;
        _startTime = DateTime.Now;

        // Ensure logs directory exists
        if (!Directory.Exists(ManageFiles.LogsFolderPath))
            Directory.CreateDirectory(ManageFiles.LogsFolderPath);

        // Register all handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Log that diagnostics are initialized
        LogShutdownEvent("DIAGNOSTICS_INIT", "Shutdown diagnostics initialized", $"Process ID: {Environment.ProcessId}, Start Time: {_startTime}");
    }

    /// <summary>
    /// Registers IHostApplicationLifetime events for additional shutdown tracking.
    /// Call this after building the host.
    /// </summary>
    public static void RegisterHostLifetimeEvents(IHostApplicationLifetime lifetime)
    {
        lifetime.ApplicationStarted.Register(() =>
        {
            LogShutdownEvent("HOST_STARTED", "Host application started", null);
        });

        lifetime.ApplicationStopping.Register(() =>
        {
            _lastShutdownReason = "ApplicationStopping event triggered";
            LogShutdownEvent("HOST_STOPPING", "Host application stopping", GetShutdownContext());
        });

        lifetime.ApplicationStopped.Register(() =>
        {
            LogShutdownEvent("HOST_STOPPED", "Host application stopped", $"Uptime: {GetUptime()}");
        });
    }

    /// <summary>
    /// Records a manual shutdown reason (e.g., from StopAsync or service control).
    /// </summary>
    public static void SetShutdownReason(string reason)
    {
        _lastShutdownReason = reason;
    }

    /// <summary>
    /// Logs a shutdown-related event to the dedicated shutdown log.
    /// </summary>
    public static void LogShutdownEvent(string eventType, string message, string? details)
    {
        try
        {
            lock (_logLock)
            {
                var logEntry = new StringBuilder();
                logEntry.AppendLine($"=== {eventType} === {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                logEntry.AppendLine($"Message: {message}");
                if (!string.IsNullOrEmpty(details))
                    logEntry.AppendLine($"Details: {details}");
                logEntry.AppendLine();

                File.AppendAllText(ShutdownLogPath, logEntry.ToString());

                // Also log to the regular application log
                var regularLogPath = Path.Combine(ManageFiles.LogsFolderPath, $"{DateTime.Now:MMddyyyy}_Logfile.txt");
                ManageFiles.CreateAndAppendLogs(regularLogPath, $"{DateTime.Now} [{eventType}] {message}" + (string.IsNullOrEmpty(details) ? "" : $" - {details}"));
            }
        }
        catch
        {
            // Last resort - try to write to a simple text file
            try
            {
                File.AppendAllText(Path.Combine(ManageFiles.LogsFolderPath, "emergency_shutdown.log"),
                    $"{DateTime.Now}: {eventType} - {message}\r\n");
            }
            catch { }
        }
    }

    /// <summary>
    /// Captures and logs unhandled exceptions that would crash the service.
    /// </summary>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        _lastShutdownReason = $"Unhandled exception: {exception?.GetType().Name ?? "Unknown"}";

        var details = new StringBuilder();
        details.AppendLine($"IsTerminating: {e.IsTerminating}");
        details.AppendLine($"Exception Type: {exception?.GetType().FullName ?? "Unknown"}");
        details.AppendLine($"Message: {exception?.Message ?? "No message"}");
        details.AppendLine($"Stack Trace:\r\n{exception?.StackTrace ?? "No stack trace"}");

        if (exception?.InnerException != null)
        {
            details.AppendLine($"Inner Exception: {exception.InnerException.GetType().FullName}");
            details.AppendLine($"Inner Message: {exception.InnerException.Message}");
            details.AppendLine($"Inner Stack Trace:\r\n{exception.InnerException.StackTrace}");
        }

        LogShutdownEvent("UNHANDLED_EXCEPTION", $"Unhandled exception occurred (Terminating: {e.IsTerminating})", details.ToString());

        // Track to Vitalytics as unexpected termination (will be classified as "crash" for alerting)
        try
        {
            VitalyticsService.Instance.TrackUnexpectedTermination(
                $"Unhandled exception: {exception?.GetType().Name ?? "Unknown"}",
                exception);
            // Give Vitalytics time to flush
            Thread.Sleep(2000);
        }
        catch { }
    }

    /// <summary>
    /// Captures process exit events.
    /// </summary>
    private static void OnProcessExit(object? sender, EventArgs e)
    {
        int exitCode = Environment.ExitCode;
        bool isUnexpected = exitCode != 0 ||
                           (!_lastShutdownReason.Contains("CancellationRequested") &&
                            !_lastShutdownReason.Contains("SystemShutdown") &&
                            !_lastShutdownReason.Contains("ServiceControlManager"));

        LogShutdownEvent("PROCESS_EXIT", $"Process exit event triggered (Unexpected: {isUnexpected})",
            $"Last Known Reason: {_lastShutdownReason}, Uptime: {GetUptime()}, Exit Code: {exitCode}");

        // Track unexpected exits to Vitalytics as crashes
        if (isUnexpected)
        {
            try
            {
                VitalyticsService.Instance.TrackUnexpectedTermination(
                    $"Process exit with code {exitCode}: {_lastShutdownReason}");
            }
            catch { }
        }
    }

    /// <summary>
    /// Captures unobserved task exceptions (async exceptions that weren't awaited).
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var details = new StringBuilder();
        details.AppendLine($"Observed: {e.Observed}");

        if (e.Exception != null)
        {
            details.AppendLine($"Exception Type: {e.Exception.GetType().FullName}");
            details.AppendLine($"Message: {e.Exception.Message}");

            // Log all inner exceptions from AggregateException
            foreach (var innerEx in e.Exception.Flatten().InnerExceptions)
            {
                details.AppendLine($"--- Inner Exception ---");
                details.AppendLine($"Type: {innerEx.GetType().FullName}");
                details.AppendLine($"Message: {innerEx.Message}");
                details.AppendLine($"Stack Trace:\r\n{innerEx.StackTrace}");
            }
        }

        LogShutdownEvent("UNOBSERVED_TASK_EXCEPTION", "Unobserved task exception detected", details.ToString());

        // Mark as observed to prevent process termination
        e.SetObserved();
    }

    /// <summary>
    /// Gets context information about the current shutdown.
    /// </summary>
    private static string GetShutdownContext()
    {
        var context = new StringBuilder();
        context.AppendLine($"Reason: {_lastShutdownReason}");
        context.AppendLine($"Uptime: {GetUptime()}");
        context.AppendLine($"Process ID: {Environment.ProcessId}");
        context.AppendLine($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
        context.AppendLine($"Thread Count: {Process.GetCurrentProcess().Threads.Count}");

        // Try to get system shutdown info
        try
        {
            var systemInfo = new StringBuilder();
            systemInfo.AppendLine($"Machine: {Environment.MachineName}");
            systemInfo.AppendLine($"OS: {Environment.OSVersion}");
            systemInfo.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
            systemInfo.AppendLine($"User Interactive: {Environment.UserInteractive}");
            context.Append(systemInfo);
        }
        catch { }

        return context.ToString();
    }

    /// <summary>
    /// Gets the service uptime as a formatted string.
    /// </summary>
    private static string GetUptime()
    {
        var uptime = DateTime.Now - _startTime;
        return $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";
    }

    /// <summary>
    /// Queries Windows Event Log for recent service-related events.
    /// Returns the last N events related to this service.
    /// </summary>
    public static string GetRecentServiceEvents(int maxEvents = 10)
    {
        var events = new StringBuilder();
        try
        {
            // Query System event log for Service Control Manager events about our service
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Service Control Manager'] and (EventID=7034 or EventID=7035 or EventID=7036 or EventID=7040 or EventID=7045)]]");

            using var reader = new EventLogReader(query);
            var relevantEvents = new List<(DateTime Time, string Message)>();
            EventRecord? record;

            while ((record = reader.ReadEvent()) != null)
            {
                using (record)
                {
                    var description = record.FormatDescription() ?? "";
                    if (description.Contains("Speech Live", StringComparison.OrdinalIgnoreCase) ||
                        description.Contains("SpeechLive", StringComparison.OrdinalIgnoreCase))
                    {
                        relevantEvents.Add((record.TimeCreated ?? DateTime.MinValue, description));
                    }
                }
            }

            // Take the most recent events
            foreach (var evt in relevantEvents.OrderByDescending(e => e.Time).Take(maxEvents))
            {
                events.AppendLine($"[{evt.Time:yyyy-MM-dd HH:mm:ss}] {evt.Message}");
            }
        }
        catch (Exception ex)
        {
            events.AppendLine($"Unable to query event log: {ex.Message}");
        }

        return events.Length > 0 ? events.ToString() : "No relevant events found";
    }

    /// <summary>
    /// Writes a comprehensive diagnostic report to a file.
    /// Call this periodically or on shutdown.
    /// </summary>
    public static void WriteDiagnosticReport()
    {
        try
        {
            var report = new StringBuilder();
            report.AppendLine($"=== SpeechLive Helper Diagnostic Report ===");
            report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Start Time: {_startTime:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Uptime: {GetUptime()}");
            report.AppendLine();
            report.AppendLine("--- System Information ---");
            report.AppendLine($"Machine: {Environment.MachineName}");
            report.AppendLine($"OS: {Environment.OSVersion}");
            report.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
            report.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
            report.AppendLine($"Processor Count: {Environment.ProcessorCount}");
            report.AppendLine($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
            report.AppendLine();
            report.AppendLine("--- Recent Service Events (from Windows Event Log) ---");
            report.AppendLine(GetRecentServiceEvents());
            report.AppendLine();

            var reportPath = Path.Combine(ManageFiles.LogsFolderPath, "diagnostic_report.txt");
            File.WriteAllText(reportPath, report.ToString());
        }
        catch { }
    }
}
