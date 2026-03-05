using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EncryptionService;
using SpeechLiveUploader.Models;

namespace SpeechLiveUploader.Services
{
    /// <summary>
    /// Provides non-blocking event tracking to Vitalytics monitoring service.
    /// Uses singleton pattern for global access (matches existing static config pattern).
    /// </summary>
    public class VitalyticsService
    {
        // Hardcoded constants
        private const string APP_IDENTIFIER = "speechlive-windows";
        private const string ENVIRONMENT = "production";

        // Configurable via LocalConfig.Vitalytics_DataMode ("test" or "prod", default "prod")
        private bool _isTestMode = false;

        private static readonly string DEVICE_ID_FILE_PATH =
            @"C:\ProgramData\SpeechLive Helper\vitalytics_device_id.txt";

        private readonly ConcurrentQueue<VitalyticsEvent> _eventQueue;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
        private string _deviceId = string.Empty;
        private bool _isEnabled;
        private bool _isInitialized;

        // Flush settings
        private const int BATCH_SIZE_THRESHOLD = 10;
        private readonly TimeSpan _httpTimeout = TimeSpan.FromSeconds(30);

        private static VitalyticsService? _instance;
        private static readonly object _instanceLock = new object();

        /// <summary>
        /// Gets the singleton instance of VitalyticsService.
        /// </summary>
        public static VitalyticsService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new VitalyticsService();
                        }
                    }
                }
                return _instance;
            }
        }

        private VitalyticsService()
        {
            _eventQueue = new ConcurrentQueue<VitalyticsEvent>();
            _httpClient = new HttpClient { Timeout = _httpTimeout };
            _isEnabled = false;
            _isInitialized = false;
        }

        /// <summary>
        /// Initializes the service with configuration. Must be called after config is loaded.
        /// Non-blocking - logs errors but does not throw.
        /// </summary>
        public void Initialize()
        {
            try
            {
                // Check if enabled in local config (default to true if not specified)
                string enabledSetting = LocalConfig.Vitalytics_Enabled;
                _isEnabled = string.IsNullOrEmpty(enabledSetting) ||
                             enabledSetting.Equals("true", StringComparison.OrdinalIgnoreCase);

                if (!_isEnabled)
                {
                    LogLocal("Vitalytics monitoring disabled via configuration");
                    return;
                }

                // Validate required central config
                if (string.IsNullOrEmpty(CentralConfig.Vitalytics_ApiKey))
                {
                    LogLocal("WARNING: Vitalytics_ApiKey not found in central config - disabling Vitalytics");
                    _isEnabled = false;
                    return;
                }

                if (string.IsNullOrEmpty(CentralConfig.Vitalytics_ApiBaseUrl))
                {
                    LogLocal("WARNING: Vitalytics_ApiBaseUrl not found in central config - disabling Vitalytics");
                    _isEnabled = false;
                    return;
                }

                // Get or create device ID
                _deviceId = GetOrCreateDeviceId();

                // Set test mode based on config (default to prod)
                string dataMode = LocalConfig.Vitalytics_DataMode;
                _isTestMode = !string.IsNullOrEmpty(dataMode) &&
                              dataMode.Equals("test", StringComparison.OrdinalIgnoreCase);
                LogLocal($"Vitalytics data mode: {(_isTestMode ? "TEST" : "PRODUCTION")}");

                // Configure HttpClient headers
                string decryptedApiKey;
                try
                {
                    decryptedApiKey = EncryptionHelper.DecryptApiKey(CentralConfig.Vitalytics_ApiKey);
                }
                catch (FileNotFoundException ex)
                {
                    LogLocal($"CRITICAL ERROR: Cannot decrypt Vitalytics API key - secrets.json not found");
                    LogLocal($"ERROR: {ex.Message}");
                    LogLocal($"ACTION REQUIRED: Ensure secrets.json exists in C:\\ProgramData\\SpeechLive Helper\\");
                    _isEnabled = false;
                    return;
                }
                catch (Exception ex)
                {
                    LogLocal($"CRITICAL ERROR: Failed to decrypt Vitalytics API key - {ex.Message}");
                    LogLocal($"POSSIBLE CAUSE: secrets.json may contain a different encryption key than was used to encrypt the API token");
                    _isEnabled = false;
                    return;
                }
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("X-API-Key", decryptedApiKey);
                _httpClient.DefaultRequestHeaders.Add("X-App-Identifier", APP_IDENTIFIER);
                _httpClient.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                _isInitialized = true;
                LogLocal($"Vitalytics initialized - DeviceId: {_deviceId}, Endpoint: {CentralConfig.Vitalytics_ApiBaseUrl}");
            }
            catch (Exception ex)
            {
                LogLocal($"ERROR: Vitalytics initialization failed - {ex.Message}");
                _isEnabled = false;
            }
        }

        /// <summary>
        /// Gets or creates a persistent device ID for this installation.
        /// </summary>
        private string GetOrCreateDeviceId()
        {
            try
            {
                if (File.Exists(DEVICE_ID_FILE_PATH))
                {
                    string existingId = File.ReadAllText(DEVICE_ID_FILE_PATH).Trim();
                    if (!string.IsNullOrEmpty(existingId))
                        return existingId;
                }

                // Generate new GUID-based device ID with win- prefix
                string newId = $"win-{Guid.NewGuid()}";
                File.WriteAllText(DEVICE_ID_FILE_PATH, newId);
                LogLocal($"Generated new Vitalytics device ID: {newId}");
                return newId;
            }
            catch (Exception ex)
            {
                LogLocal($"WARNING: Could not persist device ID - {ex.Message}");
                return $"win-{Guid.NewGuid()}"; // Use transient ID
            }
        }

        #region Event Tracking Methods

        /// <summary>
        /// Queues an event for sending to Vitalytics. Non-blocking.
        /// </summary>
        public void TrackEvent(string level, string message, Dictionary<string, object>? context = null, string[]? stackTrace = null)
        {
            if (!_isEnabled || !_isInitialized)
            {
                LogLocal($"TrackEvent skipped - Enabled: {_isEnabled}, Initialized: {_isInitialized}");
                return;
            }

            try
            {
                LogLocal($"Queuing event: [{level}] {message}");
                var evt = new VitalyticsEvent
                {
                    Level = level,
                    Message = message.Length > 1000 ? message.Substring(0, 1000) : message,
                    Context = context,
                    StackTrace = stackTrace
                };

                _eventQueue.Enqueue(evt);

                // Immediate flush for errors/critical events
                if (level == "error" || level == "crash")
                {
                    _ = FlushAsync(); // Fire and forget
                }
                // Batch flush when queue reaches threshold
                else if (_eventQueue.Count >= BATCH_SIZE_THRESHOLD)
                {
                    _ = FlushAsync();
                }
            }
            catch (Exception ex)
            {
                LogLocal($"WARNING: Failed to queue Vitalytics event - {ex.Message}");
            }
        }

        /// <summary>
        /// Tracks service startup event.
        /// </summary>
        public void TrackServiceStartup()
        {
            TrackEvent("info", "SpeechLive Upload Helper service started",
                new Dictionary<string, object>
                {
                    { "app_identifier", LocalConfig.APP_Identifier ?? "unknown" },
                    { "machine_name", Environment.MachineName }
                });
            // Flush immediately on startup so we know the service is running
            _ = FlushAsync();
        }

        /// <summary>
        /// Tracks graceful service shutdown event (manual stop via SCM or system shutdown).
        /// </summary>
        public void TrackServiceShutdown()
        {
            TrackServiceShutdown(isGraceful: true, reason: "Service stopped gracefully");
        }

        /// <summary>
        /// Tracks service shutdown event with reason classification.
        /// </summary>
        /// <param name="isGraceful">True if this was a manual/expected stop, false if unexpected</param>
        /// <param name="reason">Description of why the service is stopping</param>
        public void TrackServiceShutdown(bool isGraceful, string reason)
        {
            // Unexpected shutdowns are classified as "crash" for alerting
            string level = isGraceful ? "info" : "crash";
            string message = isGraceful
                ? "SpeechLive Upload Helper service stopped"
                : "SpeechLive Upload Helper service stopped unexpectedly";

            TrackEvent(level, message,
                new Dictionary<string, object>
                {
                    { "shutdown_reason", reason },
                    { "is_graceful", isGraceful },
                    { "machine_name", Environment.MachineName },
                    { "app_identifier", LocalConfig.APP_Identifier ?? "unknown" }
                });

            // Force flush on shutdown with timeout
            try
            {
                FlushAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore flush errors during shutdown
            }
        }

        /// <summary>
        /// Tracks unexpected service termination (crash). Call this from unhandled exception handlers.
        /// </summary>
        public void TrackUnexpectedTermination(string reason, Exception? exception = null)
        {
            var context = new Dictionary<string, object>
            {
                { "termination_reason", reason },
                { "machine_name", Environment.MachineName },
                { "app_identifier", LocalConfig.APP_Identifier ?? "unknown" }
            };

            string[]? stackTrace = null;

            if (exception != null)
            {
                context["exception_type"] = exception.GetType().Name;
                context["exception_message"] = exception.Message;

                var stackTraceLines = exception.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                var trimmedStackTrace = new List<string>();
                foreach (var line in stackTraceLines.Take(50))
                {
                    trimmedStackTrace.Add(line.Trim());
                }
                if (exception.InnerException != null)
                {
                    trimmedStackTrace.Add($"--- Inner Exception: {exception.InnerException.GetType().Name} ---");
                    trimmedStackTrace.Add(exception.InnerException.Message);
                }
                stackTrace = trimmedStackTrace.ToArray();
            }

            TrackEvent("crash", $"Service terminated unexpectedly: {reason}", context, stackTrace);

            // Immediate flush - this is critical
            try
            {
                FlushAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore flush errors during crash
            }
        }

        /// <summary>
        /// Tracks heartbeat event.
        /// </summary>
        public void TrackHeartbeat()
        {
            TrackEvent("heartbeat", "Service heartbeat",
                new Dictionary<string, object>
                {
                    { "uptime_check", "active" }
                });
            // Flush immediately - heartbeats are health checks
            _ = FlushAsync();
        }

        /// <summary>
        /// Tracks successful file upload.
        /// </summary>
        public void TrackUploadSuccess(string fileName, string authorId, string fileSource)
        {
            TrackEvent("info", $"File uploaded successfully: {fileName}",
                new Dictionary<string, object>
                {
                    { "file_name", fileName },
                    { "author_id", authorId },
                    { "source", fileSource }
                });
            // Flush immediately so uploads are tracked in real-time
            _ = FlushAsync();
        }

        /// <summary>
        /// Tracks failed file upload.
        /// </summary>
        public void TrackUploadFailure(string fileName, string errorDetails, int statusCode)
        {
            TrackEvent("error", $"File upload failed: {fileName}",
                new Dictionary<string, object>
                {
                    { "file_name", fileName },
                    { "error_details", errorDetails },
                    { "status_code", statusCode }
                });
        }

        /// <summary>
        /// Tracks author mapping failure.
        /// </summary>
        public void TrackAuthorMappingFailure(string fileName, string authorName)
        {
            TrackEvent("crash", $"Author mapping not found: {authorName}",
                new Dictionary<string, object>
                {
                    { "file_name", fileName },
                    { "author_name", authorName }
                });
            // Flush immediately - this is a critical error
            _ = FlushAsync();
        }

        /// <summary>
        /// Tracks configuration error.
        /// </summary>
        public void TrackConfigurationError(string errorType, string details)
        {
            TrackEvent("error", $"Configuration error: {errorType}",
                new Dictionary<string, object>
                {
                    { "error_type", errorType },
                    { "details", details }
                });
        }

        /// <summary>
        /// Tracks critical exception.
        /// </summary>
        public void TrackCriticalException(Exception ex, string context)
        {
            var stackTraceLines = ex.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            var trimmedStackTrace = new List<string>();
            foreach (var line in stackTraceLines.Take(50))
            {
                trimmedStackTrace.Add(line.Trim());
            }

            // Include inner exception if present
            if (ex.InnerException != null)
            {
                trimmedStackTrace.Add($"--- Inner Exception: {ex.InnerException.GetType().Name} ---");
                trimmedStackTrace.Add(ex.InnerException.Message);
            }

            TrackEvent("crash", $"Critical exception in {context}: {ex.Message}",
                new Dictionary<string, object>
                {
                    { "context", context },
                    { "exception_type", ex.GetType().Name },
                    { "exception_message", ex.Message }
                },
                trimmedStackTrace.ToArray());
        }

        /// <summary>
        /// Tracks log maintenance operations (archiving and purging).
        /// </summary>
        public void TrackLogMaintenance(int filesArchived, int filesPurged, long bytesFreed)
        {
            TrackEvent("info", "Log maintenance completed",
                new Dictionary<string, object>
                {
                    { "files_archived", filesArchived },
                    { "files_purged", filesPurged },
                    { "bytes_freed", bytesFreed },
                    { "bytes_freed_mb", Math.Round(bytesFreed / (1024.0 * 1024.0), 2) }
                });
        }

        #endregion

        #region Flush

        /// <summary>
        /// Flushes all queued events to Vitalytics API. Non-blocking, fire-and-forget.
        /// </summary>
        public async Task FlushAsync()
        {
            LogLocal($"FlushAsync called - Enabled: {_isEnabled}, Initialized: {_isInitialized}, QueueEmpty: {_eventQueue.IsEmpty}");

            if (!_isEnabled || !_isInitialized || _eventQueue.IsEmpty)
                return;

            // Prevent concurrent flushes
            if (!await _flushLock.WaitAsync(0))
            {
                LogLocal("FlushAsync skipped - lock not available (another flush in progress)");
                return;
            }
            LogLocal("FlushAsync - lock acquired");

            try
            {
                var eventsToSend = new List<VitalyticsEvent>();

                // Drain the queue (max 100 events per batch)
                while (_eventQueue.TryDequeue(out var evt) && eventsToSend.Count < 100)
                {
                    eventsToSend.Add(evt);
                }

                if (eventsToSend.Count == 0)
                    return;

                var request = new VitalyticsRequest
                {
                    AppIdentifier = APP_IDENTIFIER,
                    Environment = ENVIRONMENT,
                    IsTest = _isTestMode,
                    Events = eventsToSend,
                    DeviceInfo = new VitalyticsDeviceInfo
                    {
                        DeviceId = _deviceId,
                        DeviceModel = LocalConfig.APP_Identifier ?? Environment.MachineName,
                        OsVersion = $"Windows {Environment.OSVersion.Version}",
                        AppVersion = GetAppVersion(),
                        BuildNumber = GetBuildNumber(),
                        Platform = "windows"
                    }
                };

                string jsonPayload = JsonSerializer.Serialize(request, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                string endpoint = $"{CentralConfig.Vitalytics_ApiBaseUrl.TrimEnd('/')}/health/events";

                LogLocal($"Sending {eventsToSend.Count} event(s) to {endpoint}");

                var response = await _httpClient.PostAsync(endpoint, content);

                LogLocal($"Vitalytics API response: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    LogLocal($"WARNING: Vitalytics API returned {response.StatusCode}: {responseBody}");

                    // Re-queue events on server errors (not auth errors)
                    if ((int)response.StatusCode >= 500)
                    {
                        foreach (var evt in eventsToSend)
                        {
                            _eventQueue.Enqueue(evt);
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                LogLocal($"WARNING: Vitalytics network error - {ex.Message}");
                // Don't re-queue on network errors to prevent infinite growth
            }
            catch (Exception ex)
            {
                LogLocal($"WARNING: Vitalytics flush failed - {ex.Message}");
            }
            finally
            {
                _flushLock.Release();
                LogLocal("FlushAsync - lock released");
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Gets the application version from assembly.
        /// </summary>
        private static string GetAppVersion()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
            }
            catch
            {
                return "1.0.0";
            }
        }

        /// <summary>
        /// Gets the build number from assembly.
        /// </summary>
        private static string? GetBuildNumber()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return version?.Revision.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Logs to local file (follows existing pattern).
        /// </summary>
        private void LogLocal(string message)
        {
            try
            {
                string logPath = Path.Combine(ManageFiles.LogsFolderPath,
                    DateTime.Now.Date.ToString("MMddyyyy") + "_Logfile.txt");
                ManageFiles.CreateAndAppendLogs(logPath, $"{DateTime.Now} [VITALYTICS] {message}");
            }
            catch
            {
                // Fail silently - can't log if logging fails
            }
        }

        #endregion
    }
}
