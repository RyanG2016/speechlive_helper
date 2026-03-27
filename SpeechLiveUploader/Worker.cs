using System.IO;
using System.Security.Cryptography;
using Usb.Events;
using static System.Net.WebRequestMethods;
using File = System.IO.File;
using System.Media;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.Eventing.Reader;
using SpeechLiveUploader.Services;

namespace SpeechLiveUploader
{
    public enum FileSource
    {
        USB,
        Import
    }

    public class Worker : BackgroundService
    {
        static bool isProcessing = false;
        private static readonly object _processingLock = new object();
        private static bool _pendingImportCheck = false;
        private readonly ILogger<Worker> _logger;
        private FileSystemWatcher? _importFolderWatcher;
        private System.Threading.Timer? _heartbeatTimer;
        private IUsbEventWatcher? _usbEventWatcher; // Keep reference to prevent GC

        // Track recently processed files to prevent duplicates (e.g., when USB is reconnected quickly)
        private static readonly Dictionary<string, DateTime> _recentlyProcessedFiles = new();
        private static readonly object _recentFilesLock = new object();
        private const int DUPLICATE_PREVENTION_MINUTES = 5;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;

            if (!Directory.Exists(ManageFiles.WorkingFolderPath))
                Directory.CreateDirectory(ManageFiles.WorkingFolderPath);
            if (!Directory.Exists(ManageFiles.ProcessedFolderPath))
                Directory.CreateDirectory(ManageFiles.ProcessedFolderPath);
            if (!Directory.Exists(ManageFiles.LogsFolderPath))
                Directory.CreateDirectory(ManageFiles.LogsFolderPath);
            if (!Directory.Exists(ManageFiles.ErrorFolderPath))
                Directory.CreateDirectory(ManageFiles.ErrorFolderPath);
            if (!Directory.Exists(ManageFiles.ImportFolderPath))
                Directory.CreateDirectory(ManageFiles.ImportFolderPath);

            CreateLogs(DateTime.Now.ToString() + " Application Started.");

            // Verify configuration on startup
            VerifyConfigurationOnStartup();

            // Initialize FileSystemWatcher for Import folder
            InitializeImportFolderWatcher();

            // Initialize heartbeat timer (10 minutes for Vitalytics heartbeat)
            InitializeHeartbeatTimer();

            // Initialize Vitalytics monitoring service
            InitializeVitalytics();
        }

        /// <summary>
        /// Verifies configuration files are accessible and logs key settings on application startup.
        /// </summary>
        private void VerifyConfigurationOnStartup()
        {
            CreateLogs($"{DateTime.Now} === Verifying Configuration ===");

            try
            {
                // Check local configuration
                string configurationPath = ManageFiles.ConfigurationPath;
                if (!File.Exists(configurationPath))
                {
                    CreateLogs($"{DateTime.Now} ERROR: Local configuration file not found at: {configurationPath}");
                    return;
                }

                CreateLogs($"{DateTime.Now} Local configuration file found: {configurationPath}");

                // Read local configuration
                IniFile localIniFile = new IniFile(configurationPath);
                string centralConfigPath = localIniFile.Read("Central_Config_UNC", "Config");
                string appIdentifier = localIniFile.Read("APP_Identifier", "Config");
                string deleteAfterUpload = localIniFile.Read("Delete_Files_After_Upload", "Config");
                string driveLetterToMonitor = localIniFile.Read("Drive_Letter_To_Monitor", "Config");

                CreateLogs($"{DateTime.Now} Local Config - Central UNC: {centralConfigPath}");
                CreateLogs($"{DateTime.Now} Local Config - APP Identifier: {appIdentifier}");
                CreateLogs($"{DateTime.Now} Local Config - Delete After Upload: {deleteAfterUpload}");
                CreateLogs($"{DateTime.Now} Local Config - Drive Letter to Monitor: {driveLetterToMonitor}");

                string defaultAuthorId = localIniFile.Read("DEFAULT_AUTHOR_ID", "Config");
                string alwaysFallbackSetting = localIniFile.Read("ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID", "Config");
                string alwaysFallbackValue = string.IsNullOrEmpty(alwaysFallbackSetting) ? "false" : alwaysFallbackSetting.ToLower();

                if (!string.IsNullOrEmpty(defaultAuthorId))
                {
                    CreateLogs($"{DateTime.Now} Local Config - Default Author ID: {defaultAuthorId}");
                    CreateLogs($"{DateTime.Now} Local Config - Always Fallback To Default: {alwaysFallbackValue}");
                    if (alwaysFallbackValue == "true")
                    {
                        CreateLogs($"{DateTime.Now} Local Config - Fallback applies to: BOTH USB and Import files");
                    }
                    else
                    {
                        CreateLogs($"{DateTime.Now} Local Config - Fallback applies to: Import files only");
                    }
                }
                else
                {
                    CreateLogs($"{DateTime.Now} Local Config - Default Author ID: Not configured");
                }

                // Read static worktype settings
                string staticWorktype = localIniFile.Read("STATIC_WORKTYPE", "Config");
                string useStaticWorktype = localIniFile.Read("USE_STATIC_WORKTYPE", "Config");
                string staticWorktypeValue = string.IsNullOrEmpty(staticWorktype) ? "Office Visit" : staticWorktype;
                string useStaticWorktypeValue = string.IsNullOrEmpty(useStaticWorktype) ? "true" : useStaticWorktype.ToLower();

                CreateLogs($"{DateTime.Now} Local Config - Static Worktype: {staticWorktypeValue}");
                CreateLogs($"{DateTime.Now} Local Config - Use Static Worktype: {useStaticWorktypeValue}");

                // Check central configuration
                if (string.IsNullOrEmpty(centralConfigPath))
                {
                    CreateLogs($"{DateTime.Now} ERROR: Central_Config_UNC not specified in local configuration");
                    return;
                }

                if (!File.Exists(centralConfigPath))
                {
                    CreateLogs($"{DateTime.Now} ERROR: Central configuration file not accessible at: {centralConfigPath}");
                    CreateLogs($"{DateTime.Now} Note: Verify network path is accessible and service account has permissions");
                    return;
                }

                CreateLogs($"{DateTime.Now} Central configuration file accessible: {centralConfigPath}");

                // Read central configuration
                IniFile centralIniFile = new IniFile(centralConfigPath);
                string apiBearer = centralIniFile.Read("API_Bearer", "Config");
                string apiTenant = centralIniFile.Read("API_Tenant", "Config");
                string apiUserAgent = centralIniFile.Read("API_UserAgent", "Config");

                bool hasApiBearer = !string.IsNullOrEmpty(apiBearer);
                CreateLogs($"{DateTime.Now} Central Config - API Bearer Token: {(hasApiBearer ? "Present (encrypted)" : "MISSING")}");
                CreateLogs($"{DateTime.Now} Central Config - API Tenant: {apiTenant}");
                CreateLogs($"{DateTime.Now} Central Config - API User Agent: {apiUserAgent}");

                // Load and count users
                var users = centralIniFile.GetUsers();
                if (users.Count == 0)
                {
                    CreateLogs($"{DateTime.Now} WARNING: No user mappings found in central configuration");
                }
                else
                {
                    CreateLogs($"{DateTime.Now} Central Config - User Mappings: {users.Count} user(s) configured");
                }

                CreateLogs($"{DateTime.Now} === Configuration Verification Complete - All checks passed ===");
            }
            catch (Exception ex)
            {
                CreateLogs($"{DateTime.Now} ERROR: Configuration verification failed - {ex.Message}");
                CreateLogs($"{DateTime.Now} Exception details: {ex.ToString()}");
            }
        }

        /// <summary>
        /// Initializes the Vitalytics monitoring service.
        /// Reads Vitalytics configuration from INI files and initializes the service.
        /// </summary>
        private void InitializeVitalytics()
        {
            try
            {
                // Read Vitalytics config from local config
                string configurationPath = ManageFiles.ConfigurationPath;
                if (File.Exists(configurationPath))
                {
                    IniFile localIniFile = new IniFile(configurationPath);
                    LocalConfig.Vitalytics_Enabled = localIniFile.Read("Vitalytics_Enabled", "Config");
                    LocalConfig.Vitalytics_DataMode = localIniFile.Read("Vitalytics_DataMode", "Config");

                    // Default to enabled if not specified
                    if (string.IsNullOrEmpty(LocalConfig.Vitalytics_Enabled))
                    {
                        LocalConfig.Vitalytics_Enabled = "true";
                    }

                    // Default to prod if not specified
                    if (string.IsNullOrEmpty(LocalConfig.Vitalytics_DataMode))
                    {
                        LocalConfig.Vitalytics_DataMode = "prod";
                    }

                    // Read log retention setting (default 30 days)
                    LocalConfig.Log_Retention_Days = localIniFile.Read("Log_Retention_Days", "Config");
                    if (string.IsNullOrEmpty(LocalConfig.Log_Retention_Days))
                    {
                        LocalConfig.Log_Retention_Days = "30";
                    }

                    CreateLogs($"{DateTime.Now} Local Config - Vitalytics Enabled: {LocalConfig.Vitalytics_Enabled}");
                    CreateLogs($"{DateTime.Now} Local Config - Vitalytics Data Mode: {LocalConfig.Vitalytics_DataMode}");
                    CreateLogs($"{DateTime.Now} Local Config - Log Retention Days: {LocalConfig.Log_Retention_Days}");

                    // Read from central config if local is enabled
                    if (LocalConfig.Vitalytics_Enabled.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        string centralConfigPath = localIniFile.Read("Central_Config_UNC", "Config");
                        if (!string.IsNullOrEmpty(centralConfigPath) && File.Exists(centralConfigPath))
                        {
                            IniFile centralIniFile = new IniFile(centralConfigPath);
                            CentralConfig.Vitalytics_ApiKey = centralIniFile.Read("Vitalytics_ApiKey", "Config");
                            CentralConfig.Vitalytics_ApiBaseUrl = centralIniFile.Read("Vitalytics_ApiBaseUrl", "Config");

                            bool hasApiKey = !string.IsNullOrEmpty(CentralConfig.Vitalytics_ApiKey);
                            CreateLogs($"{DateTime.Now} Central Config - Vitalytics API Key: {(hasApiKey ? "Present (encrypted)" : "MISSING")}");
                            CreateLogs($"{DateTime.Now} Central Config - Vitalytics API URL: {CentralConfig.Vitalytics_ApiBaseUrl}");
                        }
                    }
                }

                // Initialize the Vitalytics service
                VitalyticsService.Instance.Initialize();

                // Track service startup
                VitalyticsService.Instance.TrackServiceStartup();

                CreateLogs($"{DateTime.Now} Vitalytics monitoring initialized.");
            }
            catch (Exception ex)
            {
                CreateLogs($"{DateTime.Now} WARNING: Vitalytics initialization failed - {ex.Message}");
                // Non-blocking - continue service operation
            }
        }

        /// <summary>
        /// Performs log maintenance: archives previous days' logs and purges old archived logs based on retention policy.
        /// </summary>
        private void PerformLogMaintenance()
        {
            try
            {
                CreateLogs($"{DateTime.Now} Starting log maintenance...");

                int filesArchived = 0;
                int filesPurged = 0;
                long bytesFreed = 0;

                // Parse retention days (default 30)
                int retentionDays = 30;
                if (int.TryParse(LocalConfig.Log_Retention_Days, out int parsedDays) && parsedDays > 0)
                {
                    retentionDays = parsedDays;
                }

                string logsFolder = ManageFiles.LogsFolderPath;
                string archiveFolder = ManageFiles.LogsArchiveFolderPath;

                // Ensure archive folder exists
                if (!Directory.Exists(archiveFolder))
                {
                    Directory.CreateDirectory(archiveFolder);
                    CreateLogs($"{DateTime.Now} Created log archive folder: {archiveFolder}");
                }

                // Get today's date for comparison
                string todayPrefix = DateTime.Now.ToString("MMddyyyy");

                // === Step 1: Archive previous days' logs ===
                if (Directory.Exists(logsFolder))
                {
                    var logFiles = Directory.GetFiles(logsFolder, "*.txt", SearchOption.TopDirectoryOnly);
                    foreach (var logFile in logFiles)
                    {
                        string fileName = Path.GetFileName(logFile);

                        // Skip if it's today's log file (either main or .1 backup)
                        if (fileName.StartsWith(todayPrefix))
                        {
                            continue;
                        }

                        // Move to archive
                        try
                        {
                            string destPath = Path.Combine(archiveFolder, fileName);

                            // If file already exists in archive, delete it first (shouldn't happen normally)
                            if (File.Exists(destPath))
                            {
                                File.Delete(destPath);
                            }

                            File.Move(logFile, destPath);
                            filesArchived++;
                            CreateLogs($"{DateTime.Now} Archived log file: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            CreateLogs($"{DateTime.Now} WARNING: Failed to archive {fileName}: {ex.Message}");
                        }
                    }
                }

                // === Step 2: Purge old archived logs based on retention ===
                if (Directory.Exists(archiveFolder))
                {
                    DateTime cutoffDate = DateTime.Now.AddDays(-retentionDays);
                    var archivedFiles = Directory.GetFiles(archiveFolder, "*.txt");

                    foreach (var archivedFile in archivedFiles)
                    {
                        try
                        {
                            FileInfo fileInfo = new FileInfo(archivedFile);

                            // Check file's last write time against retention cutoff
                            if (fileInfo.LastWriteTime < cutoffDate)
                            {
                                long fileSize = fileInfo.Length;
                                string fileName = fileInfo.Name;

                                File.Delete(archivedFile);
                                filesPurged++;
                                bytesFreed += fileSize;
                                CreateLogs($"{DateTime.Now} Purged old log file: {fileName} (Age: {(DateTime.Now - fileInfo.LastWriteTime).Days} days, Size: {fileSize} bytes)");
                            }
                        }
                        catch (Exception ex)
                        {
                            CreateLogs($"{DateTime.Now} WARNING: Failed to purge {Path.GetFileName(archivedFile)}: {ex.Message}");
                        }
                    }
                }

                // === Step 3: Report results ===
                if (filesArchived > 0 || filesPurged > 0)
                {
                    string bytesFreedDisplay = bytesFreed < 1024 ? $"{bytesFreed} bytes" :
                                               bytesFreed < 1024 * 1024 ? $"{bytesFreed / 1024.0:F2} KB" :
                                               $"{bytesFreed / (1024.0 * 1024.0):F2} MB";

                    CreateLogs($"{DateTime.Now} Log maintenance completed: {filesArchived} files archived, {filesPurged} files purged ({bytesFreedDisplay} freed)");

                    // Track to Vitalytics
                    VitalyticsService.Instance.TrackLogMaintenance(filesArchived, filesPurged, bytesFreed);
                }
                else
                {
                    CreateLogs($"{DateTime.Now} Log maintenance completed: No files to archive or purge.");
                }
            }
            catch (Exception ex)
            {
                CreateLogs($"{DateTime.Now} ERROR: Log maintenance failed - {ex.Message}");
                // Non-blocking - continue service operation
            }
        }

        /// <summary>
        /// Handles service shutdown - tracks shutdown event and flushes Vitalytics events.
        /// Captures detailed information about why the service is stopping.
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            // Determine the likely shutdown reason and whether it's graceful
            var (shutdownReason, isGraceful) = DetermineShutdownReason(cancellationToken);
            ShutdownDiagnostics.SetShutdownReason(shutdownReason);

            CreateLogs($"{DateTime.Now} === SERVICE SHUTDOWN ===");
            CreateLogs($"{DateTime.Now} Shutdown Reason: {shutdownReason}");
            CreateLogs($"{DateTime.Now} Is Graceful: {isGraceful}");
            CreateLogs($"{DateTime.Now} Cancellation Requested: {cancellationToken.IsCancellationRequested}");

            // Log service state at shutdown
            try
            {
                var process = Process.GetCurrentProcess();
                CreateLogs($"{DateTime.Now} Shutdown Context - Working Set: {process.WorkingSet64 / 1024 / 1024} MB, Thread Count: {process.Threads.Count}");
            }
            catch { }

            // Log shutdown event to dedicated shutdown log with Windows Event Log info
            ShutdownDiagnostics.LogShutdownEvent("SERVICE_STOPASYNC", $"StopAsync called - {shutdownReason} (Graceful: {isGraceful})",
                $"Recent Windows Events:\n{ShutdownDiagnostics.GetRecentServiceEvents(5)}");

            // Write diagnostic report before shutdown
            ShutdownDiagnostics.WriteDiagnosticReport();

            // Track shutdown to Vitalytics - non-graceful shutdowns are classified as "crash" for alerting
            VitalyticsService.Instance.TrackServiceShutdown(isGraceful, shutdownReason);

            // Dispose heartbeat timer
            _heartbeatTimer?.Dispose();

            // Dispose FileSystemWatcher
            _importFolderWatcher?.Dispose();

            // Dispose USB event watcher
            if (_usbEventWatcher is IDisposable disposableWatcher)
            {
                disposableWatcher.Dispose();
            }
            _usbEventWatcher = null;

            CreateLogs($"{DateTime.Now} Service shutdown complete.");

            await base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// Attempts to determine why the service is being stopped and whether it's graceful.
        /// </summary>
        /// <returns>Tuple of (reason string, isGraceful bool)</returns>
        private (string reason, bool isGraceful) DetermineShutdownReason(CancellationToken cancellationToken)
        {
            var reasons = new List<string>();
            bool isGraceful = false;

            // Check if it's a cancellation request (graceful stop from SCM or host)
            if (cancellationToken.IsCancellationRequested)
            {
                reasons.Add("CancellationRequested");
                isGraceful = true; // This is the normal way services are stopped
            }

            // Check for system shutdown
            try
            {
                if (Environment.HasShutdownStarted)
                {
                    reasons.Add("SystemShutdownInProgress");
                    isGraceful = true; // System shutdown is expected
                }
            }
            catch { }

            // Check recent Windows Event Log for clues
            try
            {
                var recentEvents = ShutdownDiagnostics.GetRecentServiceEvents(3);
                if (recentEvents.Contains("stop", StringComparison.OrdinalIgnoreCase))
                {
                    reasons.Add("ServiceControlManager_StopRequested");
                    isGraceful = true; // Manual stop via SCM is graceful
                }
            }
            catch { }

            if (reasons.Count == 0)
            {
                // StopAsync was called but we don't know why - this is unexpected
                return ("Unknown reason (StopAsync called without cancellation)", false);
            }

            return (string.Join(", ", reasons), isGraceful);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            CreateLogs($"{DateTime.Now} ExecuteAsync started - initializing service components...");

            // Perform log maintenance (archive old logs, purge expired logs)
            PerformLogMaintenance();

            // Check Import folder for any existing files on startup
            CheckImportFolderOnStartup();

            // Initialize USB event monitoring
            CheckForEvents();

            CreateLogs($"{DateTime.Now} Service initialization complete. Entering monitoring loop.");

            // Keep the service running by waiting for cancellation
            // The actual work is done via event handlers (USB watcher, Import folder watcher, heartbeat timer)
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for a long period or until cancellation
                    // This keeps ExecuteAsync alive without consuming resources
                    await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected when stoppingToken is cancelled - exit gracefully
                    CreateLogs($"{DateTime.Now} ExecuteAsync received cancellation signal.");
                    break;
                }
            }

            CreateLogs($"{DateTime.Now} ExecuteAsync exiting.");
        }

        /// <summary>
        /// Checks the Import folder for any existing files when the service starts.
        /// Processes any .ds2 or .dss files found.
        /// </summary>
        private void CheckImportFolderOnStartup()
        {
            try
            {
                var ds2Files = Directory.GetFiles(ManageFiles.ImportFolderPath, "*.ds2");
                var dssFiles = Directory.GetFiles(ManageFiles.ImportFolderPath, "*.dss");

                if (ds2Files.Length > 0 || dssFiles.Length > 0)
                {
                    CreateLogs($"{DateTime.Now} Found {ds2Files.Length + dssFiles.Length} file(s) in Import folder on startup.");

                    // Set processing flag to prevent concurrent processing from watcher events
                    lock (_processingLock)
                    {
                        isProcessing = true;
                    }

                    ReadINIFiles(ManageFiles.ImportFolderPath, FileSource.Import);
                }
            }
            catch (Exception ex)
            {
                CreateLogs($"{DateTime.Now} Error checking Import folder on startup: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates a timestamped file name based on the provided file path.
        /// The file name is composed of the original name without extension,
        /// followed by the current date and time, and the original extension.
        /// </summary>
        /// <param name="filePath">The original file path to generate the timestamped name from.</param>
        /// <returns>A string representing the new file name with a timestamp.</returns>
        private string GenerateTimestampedFileName(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            string ext = Path.GetExtension(filePath);
            string timestamp = DateTime.Now.ToString("ddMMyy_HHmmss");
            return $"{name}_{timestamp}{ext}";
        }

        /// <summary>
        /// Generates a unique filename for the Working folder by checking if the file exists
        /// and appending a numeric suffix if needed to avoid conflicts.
        /// </summary>
        /// <param name="fileName">The desired filename to check.</param>
        /// <returns>A unique filename that doesn't conflict with existing files in Working folder.</returns>
        private string GetUniqueWorkingFileName(string fileName)
        {
            string workingFolderPath = ManageFiles.WorkingFolderPath;
            string targetPath = Path.Combine(workingFolderPath, fileName);

            // If file doesn't exist, return the original filename
            if (!File.Exists(targetPath))
                return fileName;

            // File exists, need to add suffix
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            int suffix = 1;

            while (File.Exists(Path.Combine(workingFolderPath, $"{nameWithoutExt}_{suffix}{ext}")))
            {
                suffix++;
            }

            return $"{nameWithoutExt}_{suffix}{ext}";
        }

        /// <summary>
        /// Initializes the FileSystemWatcher to monitor the Import folder for new dictation files.
        /// </summary>
        private void InitializeImportFolderWatcher()
        {
            try
            {
                _importFolderWatcher = new FileSystemWatcher(ManageFiles.ImportFolderPath);
                _importFolderWatcher.Filter = "*.*";
                _importFolderWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite;
                _importFolderWatcher.Created += OnImportFileCreated;
                _importFolderWatcher.Renamed += OnImportFileRenamed;
                _importFolderWatcher.EnableRaisingEvents = true;

                CreateLogs($"{DateTime.Now} Import folder watcher initialized.");
            }
            catch (Exception ex)
            {
                CreateLogs($"{DateTime.Now} Error initializing import folder watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Initializes a timer that logs a heartbeat message every 10 minutes to show the service is running.
        /// Also sends heartbeat to Vitalytics for remote monitoring.
        /// </summary>
        private void InitializeHeartbeatTimer()
        {
            try
            {
                // Timer fires every 10 minutes (600000 milliseconds)
                _heartbeatTimer = new System.Threading.Timer(
                    callback: HeartbeatCallback,
                    state: null,
                    dueTime: TimeSpan.FromMinutes(10),
                    period: TimeSpan.FromMinutes(10)
                );

                CreateLogs($"{DateTime.Now} Heartbeat monitor initialized (10-minute interval).");
            }
            catch (Exception ex)
            {
                CreateLogs($"{DateTime.Now} Error initializing heartbeat timer: {ex.Message}");
            }
        }

        /// <summary>
        /// Callback method for the heartbeat timer that logs a status message and sends to Vitalytics.
        /// </summary>
        private void HeartbeatCallback(object? state)
        {
            CreateLogs($"{DateTime.Now} [HEARTBEAT] Service active - monitoring USB devices and Import folder.");

            // Track heartbeat to Vitalytics
            VitalyticsService.Instance.TrackHeartbeat();
        }

        /// <summary>
        /// Handles file created events in the Import folder.
        /// Ensures file is completely written before processing.
        /// </summary>
        private void OnImportFileCreated(object sender, FileSystemEventArgs e)
        {
            ProcessImportFile(e.FullPath);
        }

        /// <summary>
        /// Handles file renamed events in the Import folder.
        /// Ensures file is completely written before processing.
        /// </summary>
        private void OnImportFileRenamed(object sender, RenamedEventArgs e)
        {
            ProcessImportFile(e.FullPath);
        }

        /// <summary>
        /// Processes a file from the Import folder after ensuring it's fully written.
        /// Uses locking to prevent concurrent processing when multiple files are copied at once.
        /// </summary>
        private void ProcessImportFile(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLower();

            // Only process .ds2 and .dss files
            if (ext != ".ds2" && ext != ".dss")
                return;

            // Wait for file to be completely written
            if (!WaitForFile(filePath))
            {
                CreateLogs($"{DateTime.Now} Timeout waiting for file to be available: {filePath}");
                return;
            }

            // Log file detection with size
            try
            {
                FileInfo fileInfo = new FileInfo(filePath);
                long fileSizeBytes = fileInfo.Length;
                string fileSizeDisplay = fileSizeBytes < 1024 ? $"{fileSizeBytes} bytes" :
                                        fileSizeBytes < 1024 * 1024 ? $"{fileSizeBytes / 1024.0:F2} KB" :
                                        $"{fileSizeBytes / (1024.0 * 1024.0):F2} MB";

                CreateLogs($"{DateTime.Now} Import folder file detected: {Path.GetFileName(filePath)} ({fileSizeDisplay})");
            }
            catch
            {
                CreateLogs($"{DateTime.Now} Import folder file detected: {Path.GetFileName(filePath)}");
            }

            // Prevent concurrent processing - if already processing, flag for recheck
            // The file will be picked up by the current run or a subsequent recheck
            lock (_processingLock)
            {
                if (isProcessing)
                {
                    _pendingImportCheck = true;
                    CreateLogs($"{DateTime.Now} Processing already in progress, file will be included in current or next batch.");
                    return;
                }
                isProcessing = true;
            }

            ReadINIFiles(ManageFiles.ImportFolderPath, FileSource.Import);
        }

        /// <summary>
        /// Waits for a file to be fully written and available for reading.
        /// Retries for up to 10 seconds.
        /// </summary>
        private bool WaitForFile(string filePath)
        {
            int maxRetries = 20;
            int delayMs = 500;
            int retryCount = 0;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using (FileStream fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        if (retryCount > 0)
                        {
                            CreateLogs($"{DateTime.Now} File available after {retryCount} retry attempt(s): {Path.GetFileName(filePath)}");
                        }
                        return true;
                    }
                }
                catch (IOException)
                {
                    retryCount++;
                    if (retryCount == 1 || retryCount % 5 == 0) // Log first retry and every 5th retry
                    {
                        CreateLogs($"{DateTime.Now} Waiting for file to be available (attempt {retryCount}/{maxRetries}): {Path.GetFileName(filePath)}");
                    }
                    Thread.Sleep(delayMs);
                }
                catch (Exception ex)
                {
                    CreateLogs($"{DateTime.Now} Error checking file availability: {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Clears the processing flag for Import source. Call this before any early return in ReadINIFiles
        /// to ensure the flag is properly cleared and new files can be processed.
        /// </summary>
        /// <param name="source">The source of the files being processed.</param>
        private void ClearImportProcessingFlag(FileSource source)
        {
            if (source == FileSource.Import)
            {
                lock (_processingLock)
                {
                    isProcessing = false;
                    _pendingImportCheck = false;
                }
            }
        }

        /// <summary>
        /// Checks if a file was recently processed to prevent duplicate uploads.
        /// This handles cases where USB is disconnected and reconnected quickly before file deletion completes.
        /// </summary>
        /// <param name="filePath">The full path to the file being processed.</param>
        /// <returns>True if the file was recently processed and should be skipped.</returns>
        private bool WasFileRecentlyProcessed(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            lock (_recentFilesLock)
            {
                // Clean up old entries first (older than DUPLICATE_PREVENTION_MINUTES)
                var expiredKeys = _recentlyProcessedFiles
                    .Where(kvp => (DateTime.Now - kvp.Value).TotalMinutes > DUPLICATE_PREVENTION_MINUTES)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var key in expiredKeys)
                {
                    _recentlyProcessedFiles.Remove(key);
                }

                // Check if file was recently processed
                if (_recentlyProcessedFiles.TryGetValue(fileName, out var processedTime))
                {
                    var timeSinceProcessed = DateTime.Now - processedTime;
                    if (timeSinceProcessed.TotalMinutes < DUPLICATE_PREVENTION_MINUTES)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Marks a file as recently processed to prevent duplicate uploads.
        /// </summary>
        /// <param name="filePath">The full path to the file that was processed.</param>
        private void MarkFileAsProcessed(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            lock (_recentFilesLock)
            {
                _recentlyProcessedFiles[fileName] = DateTime.Now;
            }
        }

        /// <summary>
        /// Checks if a USB disconnection caused the file to become unavailable.
        /// Returns true if the exception is due to USB disconnection (DirectoryNotFoundException or DriveNotFoundException).
        /// </summary>
        /// <param name="ex">The exception to check.</param>
        /// <param name="filePath">The file path that caused the exception (optional, for logging context).</param>
        /// <returns>True if this appears to be a USB disconnection error.</returns>
        private bool IsUsbDisconnectionError(Exception ex, string? filePath = null)
        {
            // Check for exceptions that indicate the drive is no longer available
            if (ex is DirectoryNotFoundException || ex is DriveNotFoundException)
            {
                return true;
            }

            // Also check IOException with specific messages about device not ready or disconnected
            if (ex is IOException ioEx)
            {
                string message = ioEx.Message.ToLower();
                if (message.Contains("device is not ready") ||
                    message.Contains("not find a part of the path") ||
                    message.Contains("parameter is incorrect"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the configuration files and processes the files in the specified folder.
        /// </summary>
        /// <param name="DSFilesPath">The path to the folder containing the files to process.</param>
        /// <param name="source">The source of the files (USB or Import folder).</param>
        /// <remarks>
        /// This function reads the configuration files and processes the files in the specified folder.
        /// It handles both .ds2 and .dss files.
        /// If the file is valid, it calls the API with the appropriate parameters and handles the response.
        /// For USB files: copies to Working folder and optionally deletes from USB based on config.
        /// For Import files: moves to Working folder on success, always deletes from Import.
        /// If the file is invalid, it moves the file to the error folder.
        /// </remarks>
        async void ReadINIFiles(string DSFilesPath, FileSource source)
        {
            List<string> users = new List<string>();
            string configurationPath = ManageFiles.ConfigurationPath;
            string workingFolderPath = ManageFiles.WorkingFolderPath;
            string AuthorId = "";
            bool hasError = false;
            DateTime processingStartTime = DateTime.Now;
            int successCount = 0;
            int errorCount = 0;

            // Count files before processing
            string[] ds2Files;
            string[] dssFiles;

            // For USB sources, verify the drive is actually ready before attempting enumeration
            if (source == FileSource.USB)
            {
                try
                {
                    var driveInfo = new DriveInfo(Path.GetPathRoot(DSFilesPath));
                    if (!driveInfo.IsReady)
                    {
                        CreateLogs($"{DateTime.Now} WARNING: USB drive {DSFilesPath} is not ready - skipping processing.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    CreateLogs($"{DateTime.Now} WARNING: Unable to check USB drive readiness for {DSFilesPath} - {ex.Message}");
                    return;
                }
            }

            try
            {
                ds2Files = new ManageFiles().GetDs2Files(DSFilesPath);
                dssFiles = new ManageFiles().GetDsFiles(DSFilesPath);
            }
            catch (Exception ex)
            {
                if (source == FileSource.USB && IsUsbDisconnectionError(ex))
                {
                    // USB was disconnected during initial file enumeration
                    CreateLogs($"{DateTime.Now} WARNING: USB device disconnected during file enumeration - {ex.Message}");
                    CreateLogs($"{DateTime.Now} Processing aborted for this USB device.");
                }
                else
                {
                    // Unexpected enumeration error - log but don't crash
                    CreateLogs($"{DateTime.Now} ERROR: Failed to enumerate files in {DSFilesPath} - {ex.GetType().Name}: {ex.Message}");
                    CreateLogs($"{DateTime.Now} Stack Trace: {ex.StackTrace}");
                    VitalyticsService.Instance.TrackCriticalException(ex, "file_enumeration");
                }
                ClearImportProcessingFlag(source);
                return;
            }

            int totalFiles = ds2Files.Length + dssFiles.Length;

            if (totalFiles > 0)
            {
                string sourceType = source == FileSource.Import ? "import folder" : "USB device";
                CreateLogs($"{DateTime.Now} === Processing started from {sourceType}: {totalFiles} file(s) found ({ds2Files.Length} .ds2, {dssFiles.Length} .dss) ===");
            }
            else
            {
                string sourceType = source == FileSource.Import ? "import folder" : "USB device";
                CreateLogs($"{DateTime.Now} No supported files (.ds2 or .dss) found on {sourceType}.");
                ClearImportProcessingFlag(source);
                return; // Exit early if no files to process
            }

            try
            {
                IniFile iniFile = new IniFile(configurationPath);
                CreateLogs(DateTime.Now.ToString() + " Configuration File Found And Loaded.");
                LocalConfig.Central_Config_UNC = iniFile.Read("Central_Config_UNC", "Config");
                LocalConfig.APP_Identifier = iniFile.Read("APP_Identifier", "Config");
                LocalConfig.Delete_Files_After_Upload = iniFile.Read("Delete_Files_After_Upload", "Config").ToLower();
                LocalConfig.Drive_Letter_To_Monitor = iniFile.Read("Drive_Letter_To_Monitor", "Config");
                LocalConfig.DEFAULT_AUTHOR_ID = iniFile.Read("DEFAULT_AUTHOR_ID", "Config");

                // Read ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID with default of "false"
                string alwaysFallback = iniFile.Read("ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID", "Config");
                LocalConfig.ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID = string.IsNullOrEmpty(alwaysFallback) ? "false" : alwaysFallback.ToLower();

                // Read static worktype settings (default: STATIC_WORKTYPE="Office Visit", USE_STATIC_WORKTYPE="true")
                string staticWorktype = iniFile.Read("STATIC_WORKTYPE", "Config");
                LocalConfig.STATIC_WORKTYPE = string.IsNullOrEmpty(staticWorktype) ? "Office Visit" : staticWorktype;

                string useStaticWorktype = iniFile.Read("USE_STATIC_WORKTYPE", "Config");
                LocalConfig.USE_STATIC_WORKTYPE = string.IsNullOrEmpty(useStaticWorktype) ? "true" : useStaticWorktype.ToLower();

                CreateLogs(DateTime.Now.ToString() + " Configuration File Read.");
                if (!string.IsNullOrEmpty(LocalConfig.DEFAULT_AUTHOR_ID))
                {
                    CreateLogs($"{DateTime.Now} Default Author ID configured: {LocalConfig.DEFAULT_AUTHOR_ID}");
                    CreateLogs($"{DateTime.Now} Always Fallback To Default: {LocalConfig.ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID}");
                }
            }
            catch (Exception ex)
            {
                hasError = true;
                CreateLogs(DateTime.Now.ToString() + " Error reading local configuration file: " + ex.Message);
                ClearImportProcessingFlag(source);
                return; // Stop processing if local config can't be read
            }

            try
            {
                if (!File.Exists(LocalConfig.Central_Config_UNC))
                {
                    hasError = true;
                    CreateLogs(DateTime.Now.ToString() + " Central Configuration File not found at: " + LocalConfig.Central_Config_UNC);
                    ClearImportProcessingFlag(source);
                    return; // Stop processing if central config doesn't exist
                }

                IniFile iniFile = new IniFile(LocalConfig.Central_Config_UNC);
                CreateLogs(DateTime.Now.ToString() + " Central Configuration File Found And Loaded.");
                
                string encryptedApiKey = iniFile.Read("API_Bearer", "Config");
                if (string.IsNullOrEmpty(encryptedApiKey))
                {
                    hasError = true;
                    CreateLogs(DateTime.Now.ToString() + " API_Bearer not found in central configuration.");
                    ClearImportProcessingFlag(source);
                    return; // Stop processing if API key is missing
                }

                CentralConfig.API_Bearer = encryptedApiKey;
                CentralConfig.API_UserAgent = iniFile.Read("API_UserAgent", "Config");
                CentralConfig.API_Tenant = iniFile.Read("API_Tenant", "Config");
                
                if (string.IsNullOrEmpty(CentralConfig.API_Tenant))
                {
                    hasError = true;
                    CreateLogs(DateTime.Now.ToString() + " API_Tenant not found in central configuration.");
                    ClearImportProcessingFlag(source);
                    return; // Stop processing if tenant URL is missing
                }

                users = iniFile.GetUsers();
                if (users.Count == 0)
                {
                    CreateLogs(DateTime.Now.ToString() + " Warning: No users found in central configuration.");
                }
                else
                {
                    CreateLogs($"{DateTime.Now} Loaded {users.Count} user mapping(s) from central configuration.");
                }

                CreateLogs(DateTime.Now.ToString() + " Central Configuration Read Successfully.");
                CreateLogs($"{DateTime.Now} Config Details - API Tenant: {CentralConfig.API_Tenant}, User Agent: {CentralConfig.API_UserAgent}, APP Identifier: {LocalConfig.APP_Identifier}");
            }
            catch (Exception ex)
            {
                hasError = true;
                CreateLogs(DateTime.Now.ToString() + " Error reading central configuration: " + ex.Message);
                ClearImportProcessingFlag(source);
                return; // Stop processing if central config can't be read
            }

            // If we got here with an error, stop processing
            if (hasError)
            {
                CreateLogs(DateTime.Now.ToString() + " Stopping file processing due to configuration errors.");
                ClearImportProcessingFlag(source);
                return;
            }

            // === Handle DS2 Files ===
            bool usbDisconnected = false;
            if (ds2Files.Length > 0)
            {
                CreateLogs(DateTime.Now.ToString() + " Connected device contains one or more files with supported file formats (ds2)");
                foreach (var ds2 in ds2Files)
                {
                    // Check if USB was disconnected (only for USB source)
                    if (source == FileSource.USB && !File.Exists(ds2))
                    {
                        CreateLogs($"{DateTime.Now} WARNING: USB device appears to have been disconnected - file no longer accessible: {ds2}");
                        CreateLogs($"{DateTime.Now} Skipping remaining files from this USB device.");
                        usbDisconnected = true;
                        break;
                    }

                    // Check if file was recently processed (prevents duplicates when USB reconnects quickly)
                    if (WasFileRecentlyProcessed(ds2))
                    {
                        CreateLogs($"{DateTime.Now} SKIPPING {Path.GetFileName(ds2)} - file was recently processed (duplicate prevention)");
                        continue;
                    }

                    try
                    {
                        string fileName = GenerateTimestampedFileName(ds2);
                        var objm = ManageFiles.ExtractMetadata(ds2);
                        CreateLogs($"Author: {objm.Author}");
                        CreateLogs($"Worktype: {objm.WorkType}");
                        CreateLogs($"Device ID: {objm.DeviceId}");

                        // Validate metadata
                        if (string.IsNullOrEmpty(objm.Author))
                        {
                            CreateLogs($"{DateTime.Now} WARNING: Author is empty for file {ds2}");
                        }
                        if (string.IsNullOrEmpty(objm.WorkType))
                        {
                            CreateLogs($"{DateTime.Now} WARNING: WorkType is empty for file {ds2} - this may cause API issues");
                        }
                        if (string.IsNullOrEmpty(objm.DeviceId) || objm.DeviceId == "Unknown")
                        {
                            CreateLogs($"{DateTime.Now} WARNING: DeviceId is '{objm.DeviceId}' for file {ds2}");
                        }

                        AuthorId = users.FirstOrDefault(u => u.StartsWith(objm.Author + "="))?.Split('=')[1] ?? "";

                        // Use DEFAULT_AUTHOR_ID if no author mapping found
                        // Applies to: Import files always, OR USB files if ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID is true
                        bool shouldUseFallback = (source == FileSource.Import) ||
                                                (LocalConfig.ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID == "true");

                        if (string.IsNullOrEmpty(AuthorId) && shouldUseFallback && !string.IsNullOrEmpty(LocalConfig.DEFAULT_AUTHOR_ID))
                        {
                            string sourceType = source == FileSource.Import ? "Import" : "USB";
                            CreateLogs($"{DateTime.Now} {sourceType} file has no author mapping, attempting fallback to DEFAULT_AUTHOR_ID");

                            // Look up DEFAULT_AUTHOR_ID in central config users list (same as regular author lookup)
                            AuthorId = users.FirstOrDefault(u => u.StartsWith(LocalConfig.DEFAULT_AUTHOR_ID + "="))?.Split('=')[1] ?? "";

                            if (!string.IsNullOrEmpty(AuthorId))
                            {
                                CreateLogs($"{DateTime.Now} DEFAULT_AUTHOR_ID '{LocalConfig.DEFAULT_AUTHOR_ID}' found in central config, using Author ID: {AuthorId}");
                            }
                            else
                            {
                                CreateLogs($"{DateTime.Now} ERROR: DEFAULT_AUTHOR_ID '{LocalConfig.DEFAULT_AUTHOR_ID}' not found in central config [Users] section");
                                CreateLogs($"{DateTime.Now} {ds2} cannot process without valid author mapping, sending to error folder");
                            }
                        }

                        if (!string.IsNullOrEmpty(AuthorId))
                        {
                            try
                            {
                                CreateLogs(DateTime.Now.ToString() + " Calling Api");
                                await Task.Run(async () =>
                                {
                                    // For Import files, need to ensure unique filename in Working folder
                                    string workingFileName = fileName;
                                    if (source == FileSource.Import)
                                    {
                                        workingFileName = GetUniqueWorkingFileName(fileName);
                                        if (workingFileName != fileName)
                                        {
                                            CreateLogs($"{DateTime.Now} Filename conflict detected, renaming to {workingFileName}");
                                        }
                                    }

                                    File.Copy(ds2, Path.Combine(workingFolderPath, workingFileName), true);
                                    string sourceType = source == FileSource.Import ? "import folder" : "device";
                                    CreateLogs(DateTime.Now.ToString() + ds2 + $" copied from {sourceType} to application");
                                    CreateLogs(DateTime.Now.ToString() + " Calling Api As " + AuthorId);

                                    // Log API parameters before call
                                    CreateLogs($"{DateTime.Now} DEBUG: API Parameters - AuthorId: {AuthorId}, Priority: 0, WorkType: '{objm.WorkType}' (empty={string.IsNullOrEmpty(objm.WorkType)}), DeviceId: {objm.DeviceId}, FilePath: {Path.Combine(workingFolderPath, workingFileName)}");

                                    //response = await new ApiHelper().PostHistoryAsync(AuthorId, "0", objm.WorkType, objm.DeviceId, Path.Combine(workingFolderPath, fileName));
                                    CreateLogs($"{DateTime.Now} DEBUG: About to call PostHistoryAsync");
                                    HttpResponseMessage response = await new ApiHelper().PostHistoryAsync(AuthorId, "0", objm.WorkType, objm.DeviceId, Path.Combine(workingFolderPath, workingFileName));
                                    CreateLogs($"{DateTime.Now} DEBUG: PostHistoryAsync returned, Status: {response.StatusCode}");
                                    string content = await response.Content.ReadAsStringAsync();

                                    // Log full API response details
                                    if (response.IsSuccessStatusCode)
                                    {
                                        successCount++;
                                        CreateLogs($"{DateTime.Now} Api Success: {content}");
                                        CreateLogs($"{DateTime.Now} {ds2} successfully processed.");

                                        // Mark file as processed to prevent duplicates if USB reconnects quickly
                                        MarkFileAsProcessed(ds2);

                                        // Track upload success to Vitalytics
                                        VitalyticsService.Instance.TrackUploadSuccess(
                                            Path.GetFileName(ds2),
                                            AuthorId,
                                            source == FileSource.Import ? "import" : "usb");

                                        // Move file from Working to Processed folder
                                        MoveToProcessedFolder(Path.Combine(workingFolderPath, workingFileName));

                                        // For Import files, delete from Import folder after successful upload
                                        if (source == FileSource.Import)
                                        {
                                            try
                                            {
                                                CreateLogs($"{DateTime.Now} DEBUG: Attempting to delete {ds2}");
                                                FileInfo fileInfo = new FileInfo(ds2);
                                                if (fileInfo.IsReadOnly)
                                                {
                                                    fileInfo.IsReadOnly = false;
                                                }
                                                File.Delete(ds2);
                                                CreateLogs($"{DateTime.Now} {ds2} deleted from import folder.");
                                            }
                                            catch (Exception deleteEx)
                                            {
                                                CreateLogs($"{DateTime.Now} WARNING: Failed to delete {ds2} from import folder");
                                                CreateLogs($"{DateTime.Now} Delete Exception: {deleteEx.GetType().Name} - {deleteEx.Message}");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        errorCount++;
                                        hasError = true;
                                        string errorDetails = $"StatusCode: {(int)response.StatusCode} ({response.ReasonPhrase}), Response: {content}";
                                        CreateLogs($"{DateTime.Now} Api Error - {errorDetails}");
                                        CreateLogs($"{DateTime.Now} {ds2} found error while calling api, check error folder.");

                                        // Log to central error location
                                        LogErrorToCentralLocation("UPLOAD_ERROR", Path.GetFileName(ds2), errorDetails);

                                        // Track upload failure to Vitalytics
                                        VitalyticsService.Instance.TrackUploadFailure(
                                            Path.GetFileName(ds2),
                                            errorDetails,
                                            (int)response.StatusCode);

                                        File.Copy(ds2, Path.Combine(ManageFiles.ErrorFolderPath, fileName), true);
                                        File.Delete(Path.Combine(workingFolderPath, workingFileName));
                                        CreateLogs($"{DateTime.Now} {ds2} copied from {sourceType} to error folder.");

                                        // For Import files, always delete from Import after copying to Error
                                        if (source == FileSource.Import)
                                        {
                                            try
                                            {
                                                CreateLogs($"{DateTime.Now} DEBUG: Attempting to delete {ds2} (after error)");
                                                FileInfo fileInfo = new FileInfo(ds2);
                                                if (fileInfo.IsReadOnly)
                                                {
                                                    fileInfo.IsReadOnly = false;
                                                }
                                                File.Delete(ds2);
                                                CreateLogs($"{DateTime.Now} {ds2} deleted from import folder.");
                                            }
                                            catch (Exception deleteEx)
                                            {
                                                CreateLogs($"{DateTime.Now} WARNING: Failed to delete {ds2} from import folder");
                                                CreateLogs($"{DateTime.Now} Delete Exception: {deleteEx.GetType().Name} - {deleteEx.Message}");
                                            }
                                        }
                                    }

                                    // For USB files, handle deletion based on config
                                    if (source == FileSource.USB && LocalConfig.Delete_Files_After_Upload == "true")
                                    {
                                        try
                                        {
                                            CreateLogs($"{DateTime.Now} DEBUG: Attempting to delete {ds2} from USB");
                                            FileInfo fileInfo = new FileInfo(ds2);
                                            CreateLogs($"{DateTime.Now} DEBUG: File is read-only: {fileInfo.IsReadOnly}");

                                            // Clear read-only attribute if set
                                            if (fileInfo.IsReadOnly)
                                            {
                                                CreateLogs($"{DateTime.Now} DEBUG: Clearing read-only attribute on USB file");
                                                fileInfo.IsReadOnly = false;
                                            }

                                            File.Delete(ds2);
                                            CreateLogs(DateTime.Now.ToString() + ds2 + " file deleted from external device.");
                                        }
                                        catch (Exception deleteEx)
                                        {
                                            CreateLogs($"{DateTime.Now} WARNING: Failed to delete {ds2} from USB device");
                                            CreateLogs($"{DateTime.Now} Delete Exception: {deleteEx.GetType().Name} - {deleteEx.Message}");
                                            // Don't fail the whole operation just because cleanup failed
                                        }
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                hasError = true;
                                errorCount++;
                                CreateLogs($"{DateTime.Now} ERROR: Exception during file processing for {ds2}");
                                CreateLogs($"{DateTime.Now} Exception Type: {ex.GetType().Name}");
                                CreateLogs($"{DateTime.Now} Exception Message: {ex.Message}");
                                CreateLogs($"{DateTime.Now} Stack Trace: {ex.StackTrace}");
                                if (ex.InnerException != null)
                                {
                                    CreateLogs($"{DateTime.Now} Inner Exception: {ex.InnerException.Message}");
                                }

                                // Track critical exception to Vitalytics
                                VitalyticsService.Instance.TrackCriticalException(ex, "ds2_file_processing");
                            }
                        }
                        else
                        {
                            errorCount++;
                            hasError = true;
                            string errorFileName = GenerateTimestampedFileName(ds2);
                            string sourceType = source == FileSource.Import ? "import folder" : "device";
                            string authorErrorDetails = $"Author '{objm.Author}' not found in central config user mappings";
                            CreateLogs($"{DateTime.Now} {ds2} invalid author record found, check error folder.");

                            // Log to central error location
                            LogErrorToCentralLocation("AUTHOR_MAPPING", Path.GetFileName(ds2), authorErrorDetails);

                            // Track author mapping failure to Vitalytics
                            VitalyticsService.Instance.TrackAuthorMappingFailure(
                                Path.GetFileName(ds2),
                                objm.Author);

                            File.Copy(ds2, Path.Combine(ManageFiles.ErrorFolderPath, errorFileName), true);
                            CreateLogs($"{DateTime.Now} {ds2} copied from {sourceType} to error folder.");

                            // For Import files, always delete after copying to Error
                            if (source == FileSource.Import)
                            {
                                try
                                {
                                    CreateLogs($"{DateTime.Now} DEBUG: Attempting to delete {ds2} (invalid author)");
                                    FileInfo fileInfo = new FileInfo(ds2);
                                    if (fileInfo.IsReadOnly)
                                    {
                                        fileInfo.IsReadOnly = false;
                                    }
                                    File.Delete(ds2);
                                    CreateLogs($"{DateTime.Now} {ds2} deleted from import folder.");
                                }
                                catch (Exception deleteEx)
                                {
                                    CreateLogs($"{DateTime.Now} WARNING: Failed to delete {ds2} from import folder");
                                    CreateLogs($"{DateTime.Now} Delete Exception: {deleteEx.GetType().Name} - {deleteEx.Message}");
                                }
                            }
                            // For USB files, handle deletion based on config
                            else if (LocalConfig.Delete_Files_After_Upload == "true")
                            {
                                try
                                {
                                    CreateLogs($"{DateTime.Now} DEBUG: Attempting to delete {ds2} from USB (invalid author)");
                                    FileInfo fileInfo = new FileInfo(ds2);
                                    if (fileInfo.IsReadOnly)
                                    {
                                        fileInfo.IsReadOnly = false;
                                    }
                                    File.Delete(ds2);
                                    CreateLogs($"{DateTime.Now} {ds2} file deleted from external device.");
                                }
                                catch (Exception deleteEx)
                                {
                                    CreateLogs($"{DateTime.Now} WARNING: Failed to delete {ds2} from USB device");
                                    CreateLogs($"{DateTime.Now} Delete Exception: {deleteEx.GetType().Name} - {deleteEx.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Check if this is a USB disconnection error
                        if (source == FileSource.USB && IsUsbDisconnectionError(ex, ds2))
                        {
                            CreateLogs($"{DateTime.Now} WARNING: USB device disconnected during processing of {Path.GetFileName(ds2)}");
                            CreateLogs($"{DateTime.Now} Skipping remaining files from this USB device.");
                            usbDisconnected = true;
                            break;
                        }

                        hasError = true;
                        errorCount++;
                        CreateLogs($"{DateTime.Now} ERROR: Outer exception processing {ds2}");
                        CreateLogs($"{DateTime.Now} Exception Type: {ex.GetType().Name}");
                        CreateLogs($"{DateTime.Now} Exception Message: {ex.Message}");
                        CreateLogs($"{DateTime.Now} Stack Trace: {ex.StackTrace}");

                        // Track critical exception to Vitalytics
                        VitalyticsService.Instance.TrackCriticalException(ex, "ds2_outer_processing");
                    }
                }
            }
            else
            {
                //hasError = true;
                CreateLogs(DateTime.Now.ToString() + " Connected device doesn\'t contain supported file formats (ds2)");
            }

            // === Handle DSS Files ===
            // Skip DSS processing if USB was disconnected during DS2 processing
            if (usbDisconnected)
            {
                CreateLogs($"{DateTime.Now} Skipping DSS file processing due to USB disconnection.");
            }
            else if (dssFiles.Length > 0)
            {
                CreateLogs(DateTime.Now.ToString() + " Connected device contains supported file formats (dss)");
                foreach (var dss in dssFiles)
                {
                    // Check if USB was disconnected (only for USB source)
                    if (source == FileSource.USB && !File.Exists(dss))
                    {
                        CreateLogs($"{DateTime.Now} WARNING: USB device appears to have been disconnected - file no longer accessible: {dss}");
                        CreateLogs($"{DateTime.Now} Skipping remaining files from this USB device.");
                        usbDisconnected = true;
                        break;
                    }

                    // Check if file was recently processed (prevents duplicates when USB reconnects quickly)
                    if (WasFileRecentlyProcessed(dss))
                    {
                        CreateLogs($"{DateTime.Now} SKIPPING {Path.GetFileName(dss)} - file was recently processed (duplicate prevention)");
                        continue;
                    }

                    try
                    {
                        AuthorId = "";
                        string fileName = GenerateTimestampedFileName(dss);
                        var objm = ManageFiles.ExtractMetadata(dss);
                        CreateLogs($"Author: {objm.Author}");
                        CreateLogs($"Worktype: {objm.WorkType}");
                        CreateLogs($"Device ID: {objm.DeviceId}");

                        // Validate metadata
                        if (string.IsNullOrEmpty(objm.Author))
                        {
                            CreateLogs($"{DateTime.Now} WARNING: Author is empty for file {dss}");
                        }
                        if (string.IsNullOrEmpty(objm.WorkType))
                        {
                            CreateLogs($"{DateTime.Now} WARNING: WorkType is empty for file {dss} - this may cause API issues");
                        }
                        if (string.IsNullOrEmpty(objm.DeviceId) || objm.DeviceId == "Unknown")
                        {
                            CreateLogs($"{DateTime.Now} WARNING: DeviceId is '{objm.DeviceId}' for file {dss}");
                        }

                        AuthorId = users.FirstOrDefault(u => u.StartsWith(objm.Author + "="))?.Split('=')[1] ?? "";

                        // Use DEFAULT_AUTHOR_ID if no author mapping found
                        // Applies to: Import files always, OR USB files if ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID is true
                        bool shouldUseFallback = (source == FileSource.Import) ||
                                                (LocalConfig.ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID == "true");

                        if (string.IsNullOrEmpty(AuthorId) && shouldUseFallback && !string.IsNullOrEmpty(LocalConfig.DEFAULT_AUTHOR_ID))
                        {
                            string sourceType = source == FileSource.Import ? "Import" : "USB";
                            CreateLogs($"{DateTime.Now} {sourceType} file has no author mapping, attempting fallback to DEFAULT_AUTHOR_ID");

                            // Look up DEFAULT_AUTHOR_ID in central config users list (same as regular author lookup)
                            AuthorId = users.FirstOrDefault(u => u.StartsWith(LocalConfig.DEFAULT_AUTHOR_ID + "="))?.Split('=')[1] ?? "";

                            if (!string.IsNullOrEmpty(AuthorId))
                            {
                                CreateLogs($"{DateTime.Now} DEFAULT_AUTHOR_ID '{LocalConfig.DEFAULT_AUTHOR_ID}' found in central config, using Author ID: {AuthorId}");
                            }
                            else
                            {
                                CreateLogs($"{DateTime.Now} ERROR: DEFAULT_AUTHOR_ID '{LocalConfig.DEFAULT_AUTHOR_ID}' not found in central config [Users] section");
                                CreateLogs($"{DateTime.Now} {dss} cannot process without valid author mapping, sending to error folder");
                            }
                        }

                        if (!string.IsNullOrEmpty(AuthorId))
                        {
                            try
                            {
                                CreateLogs(DateTime.Now.ToString() + " Calling Api");

                                await Task.Run(async () =>
                                {
                                    // For Import files, need to ensure unique filename in Working folder
                                    string workingFileName = fileName;
                                    if (source == FileSource.Import)
                                    {
                                        workingFileName = GetUniqueWorkingFileName(fileName);
                                        if (workingFileName != fileName)
                                        {
                                            CreateLogs($"{DateTime.Now} Filename conflict detected, renaming to {workingFileName}");
                                        }
                                    }

                                    File.Copy(dss, Path.Combine(workingFolderPath, workingFileName), true);
                                    string sourceType = source == FileSource.Import ? "import folder" : "device";
                                    CreateLogs(DateTime.Now.ToString() + dss + $" copied from {sourceType} to application");
                                    CreateLogs(DateTime.Now.ToString() + " Calling Api As " + AuthorId);

                                    // Log API parameters before call
                                    CreateLogs($"{DateTime.Now} DEBUG: API Parameters - AuthorId: {AuthorId}, Priority: 0, WorkType: '{objm.WorkType}' (empty={string.IsNullOrEmpty(objm.WorkType)}), DeviceId: {objm.DeviceId}, FilePath: {Path.Combine(workingFolderPath, workingFileName)}");

                                    CreateLogs($"{DateTime.Now} DEBUG: About to call PostHistoryAsync");
                                    HttpResponseMessage response = await new ApiHelper().PostHistoryAsync(AuthorId, "0", objm.WorkType, objm.DeviceId, Path.Combine(workingFolderPath, workingFileName));
                                    CreateLogs($"{DateTime.Now} DEBUG: PostHistoryAsync returned, Status: {response.StatusCode}");
                                    string content = await response.Content.ReadAsStringAsync();

                                    // Log full API response details
                                    if (response.IsSuccessStatusCode)
                                    {
                                        successCount++;
                                        CreateLogs($"{DateTime.Now} Api Success: {content}");
                                        CreateLogs($"{DateTime.Now} {dss} successfully processed.");

                                        // Mark file as processed to prevent duplicates if USB reconnects quickly
                                        MarkFileAsProcessed(dss);

                                        // Track upload success to Vitalytics
                                        VitalyticsService.Instance.TrackUploadSuccess(
                                            Path.GetFileName(dss),
                                            AuthorId,
                                            source == FileSource.Import ? "import" : "usb");

                                        // Move file from Working to Processed folder
                                        MoveToProcessedFolder(Path.Combine(workingFolderPath, workingFileName));

                                        // For Import files, delete from Import folder after successful upload
                                        if (source == FileSource.Import)
                                        {
                                            try
                                            {
                                                CreateLogs($"{DateTime.Now} DEBUG: Attempting to delete {dss}");
                                                FileInfo fileInfo = new FileInfo(dss);
                                                if (fileInfo.IsReadOnly)
                                                {
                                                    fileInfo.IsReadOnly = false;
                                                }
                                                File.Delete(dss);
                                                CreateLogs($"{DateTime.Now} {dss} deleted from import folder.");
                                            }
                                            catch (Exception deleteEx)
                                            {
                                                CreateLogs($"{DateTime.Now} WARNING: Failed to delete {dss} from import folder");
                                                CreateLogs($"{DateTime.Now} Delete Exception: {deleteEx.GetType().Name} - {deleteEx.Message}");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        errorCount++;
                                        hasError = true;
                                        string errorDetails = $"StatusCode: {(int)response.StatusCode} ({response.ReasonPhrase}), Response: {content}";
                                        CreateLogs($"{DateTime.Now} Api Error - {errorDetails}");
                                        CreateLogs($"{DateTime.Now} {dss} found error while calling api, check error folder.");

                                        // Log to central error location
                                        LogErrorToCentralLocation("UPLOAD_ERROR", Path.GetFileName(dss), errorDetails);

                                        // Track upload failure to Vitalytics
                                        VitalyticsService.Instance.TrackUploadFailure(
                                            Path.GetFileName(dss),
                                            errorDetails,
                                            (int)response.StatusCode);

                                        File.Copy(dss, Path.Combine(ManageFiles.ErrorFolderPath, fileName), true);
                                        File.Delete(Path.Combine(workingFolderPath, workingFileName));
                                        CreateLogs($"{DateTime.Now} {dss} copied from {sourceType} to error folder.");

                                        // For Import files, always delete from Import after copying to Error
                                        if (source == FileSource.Import)
                                        {
                                            try
                                            {
                                                CreateLogs($"{DateTime.Now} DEBUG: Attempting to delete {dss} (after error)");
                                                FileInfo fileInfo = new FileInfo(dss);
                                                if (fileInfo.IsReadOnly)
                                                {
                                                    fileInfo.IsReadOnly = false;
                                                }
                                                File.Delete(dss);
                                                CreateLogs($"{DateTime.Now} {dss} deleted from import folder.");
                                            }
                                            catch (Exception deleteEx)
                                            {
                                                CreateLogs($"{DateTime.Now} WARNING: Failed to delete {dss} from import folder");
                                                CreateLogs($"{DateTime.Now} Delete Exception: {deleteEx.GetType().Name} - {deleteEx.Message}");
                                            }
                                        }
                                    }

                                    // For USB files, handle deletion based on config
                                    if (source == FileSource.USB && LocalConfig.Delete_Files_After_Upload == "true")
                                    {
                                        try
                                        {
                                            CreateLogs($"{DateTime.Now} DEBUG: Attempting to delete {dss} from USB");
                                            FileInfo fileInfo = new FileInfo(dss);
                                            CreateLogs($"{DateTime.Now} DEBUG: File is read-only: {fileInfo.IsReadOnly}");

                                            // Clear read-only attribute if set
                                            if (fileInfo.IsReadOnly)
                                            {
                                                CreateLogs($"{DateTime.Now} DEBUG: Clearing read-only attribute on USB file");
                                                fileInfo.IsReadOnly = false;
                                            }

                                            File.Delete(dss);
                                            CreateLogs(DateTime.Now.ToString() + dss + " file deleted from external device.");
                                        }
                                        catch (Exception deleteEx)
                                        {
                                            CreateLogs($"{DateTime.Now} WARNING: Failed to delete {dss} from USB device");
                                            CreateLogs($"{DateTime.Now} Delete Exception: {deleteEx.GetType().Name} - {deleteEx.Message}");
                                            // Don't fail the whole operation just because cleanup failed
                                        }
                                    }
                                });
                            }
                            catch (Exception ex)
                            {
                                hasError = true;
                                errorCount++;
                                CreateLogs($"{DateTime.Now} ERROR: Exception during file processing for {dss}");
                                CreateLogs($"{DateTime.Now} Exception Type: {ex.GetType().Name}");
                                CreateLogs($"{DateTime.Now} Exception Message: {ex.Message}");
                                CreateLogs($"{DateTime.Now} Stack Trace: {ex.StackTrace}");
                                if (ex.InnerException != null)
                                {
                                    CreateLogs($"{DateTime.Now} Inner Exception: {ex.InnerException.Message}");
                                }

                                // Track critical exception to Vitalytics
                                VitalyticsService.Instance.TrackCriticalException(ex, "dss_file_processing");
                            }
                        }
                        else
                        {
                            errorCount++;
                            hasError = true;
                            string errorFileName = GenerateTimestampedFileName(dss);
                            string sourceType = source == FileSource.Import ? "import folder" : "device";
                            string authorErrorDetails = $"Author '{objm.Author}' not found in central config user mappings";
                            CreateLogs($"{DateTime.Now} {dss} invalid author record found, check error folder.");

                            // Log to central error location
                            LogErrorToCentralLocation("AUTHOR_MAPPING", Path.GetFileName(dss), authorErrorDetails);

                            // Track author mapping failure to Vitalytics
                            VitalyticsService.Instance.TrackAuthorMappingFailure(
                                Path.GetFileName(dss),
                                objm.Author);

                            File.Copy(dss, Path.Combine(ManageFiles.ErrorFolderPath, errorFileName), true);
                            CreateLogs($"{DateTime.Now} {dss} copied from {sourceType} to error folder.");

                            // For Import files, always delete after copying to Error
                            if (source == FileSource.Import)
                            {
                                try
                                {
                                    CreateLogs($"{DateTime.Now} DEBUG: Attempting to delete {dss} (invalid author)");
                                    FileInfo fileInfo = new FileInfo(dss);
                                    if (fileInfo.IsReadOnly)
                                    {
                                        fileInfo.IsReadOnly = false;
                                    }
                                    File.Delete(dss);
                                    CreateLogs($"{DateTime.Now} {dss} deleted from import folder.");
                                }
                                catch (Exception deleteEx)
                                {
                                    CreateLogs($"{DateTime.Now} WARNING: Failed to delete {dss} from import folder");
                                    CreateLogs($"{DateTime.Now} Delete Exception: {deleteEx.GetType().Name} - {deleteEx.Message}");
                                }
                            }
                            // For USB files, handle deletion based on config
                            else if (LocalConfig.Delete_Files_After_Upload == "true")
                            {
                                try
                                {
                                    CreateLogs($"{DateTime.Now} DEBUG: Attempting to delete {dss} from USB (invalid author)");
                                    FileInfo fileInfo = new FileInfo(dss);
                                    if (fileInfo.IsReadOnly)
                                    {
                                        fileInfo.IsReadOnly = false;
                                    }
                                    File.Delete(dss);
                                    CreateLogs($"{DateTime.Now} {dss} file deleted from external device.");
                                }
                                catch (Exception deleteEx)
                                {
                                    CreateLogs($"{DateTime.Now} WARNING: Failed to delete {dss} from USB device");
                                    CreateLogs($"{DateTime.Now} Delete Exception: {deleteEx.GetType().Name} - {deleteEx.Message}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Check if this is a USB disconnection error
                        if (source == FileSource.USB && IsUsbDisconnectionError(ex, dss))
                        {
                            CreateLogs($"{DateTime.Now} WARNING: USB device disconnected during processing of {Path.GetFileName(dss)}");
                            CreateLogs($"{DateTime.Now} Skipping remaining files from this USB device.");
                            usbDisconnected = true;
                            break;
                        }

                        hasError = true;
                        errorCount++;
                        CreateLogs($"{DateTime.Now} ERROR: Outer exception processing {dss}");
                        CreateLogs($"{DateTime.Now} Exception Type: {ex.GetType().Name}");
                        CreateLogs($"{DateTime.Now} Exception Message: {ex.Message}");
                        CreateLogs($"{DateTime.Now} Stack Trace: {ex.StackTrace}");

                        // Track critical exception to Vitalytics
                        VitalyticsService.Instance.TrackCriticalException(ex, "dss_outer_processing");
                    }
                }
            }
            else
            {
                //hasError = true;
                CreateLogs(DateTime.Now.ToString() + " Connected device doesn\'t contain supported file formats (dss)");
            }

            // === Processing Summary ===
            if (totalFiles > 0)
            {
                TimeSpan processingTime = DateTime.Now - processingStartTime;
                string sourceType = source == FileSource.Import ? "import folder" : "USB device";
                CreateLogs($"{DateTime.Now} === Processing completed from {sourceType}: {successCount} successful, {errorCount} failed (Total: {totalFiles} files, Duration: {processingTime.TotalSeconds:F2}s) ===");
            }

            // === Final Audio Cue ===
            string soundPath = hasError ? ManageFiles.ErrorSoundPath : ManageFiles.ProcessCompletePath;
            Process.Start("powershell", $@"-c (New-Object Media.SoundPlayer '{soundPath}').PlaySync();");

            // === Cleanup and Recheck for Import Source ===
            if (source == FileSource.Import)
            {
                bool needsRecheck = false;
                lock (_processingLock)
                {
                    if (_pendingImportCheck)
                    {
                        // New files arrived during processing, need to recheck
                        _pendingImportCheck = false;
                        needsRecheck = true;
                        CreateLogs($"{DateTime.Now} New files detected during processing, rechecking Import folder...");
                    }
                    else
                    {
                        // No pending files, clear the processing flag
                        isProcessing = false;
                    }
                }

                if (needsRecheck)
                {
                    // Recursively process any new files that arrived during processing
                    // isProcessing remains true to prevent new events from triggering concurrent runs
                    ReadINIFiles(ManageFiles.ImportFolderPath, FileSource.Import);
                    return; // Exit this invocation, the recursive call will handle cleanup
                }
            }

            users = null;
            ds2Files = null;
            dssFiles = null;
        }

        void CheckForEvents()
        {
            // Store as class field to prevent garbage collection
            _usbEventWatcher = new UsbEventWatcher();

            //usbEventWatcher.UsbDeviceRemoved += (_, device) => {
            //    _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            //    _logger.LogInformation("Removed:" + Environment.NewLine + device + Environment.NewLine);
            //    };

            //usbEventWatcher.UsbDeviceAdded += (_, device) =>
            //{
            //    _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            //    _logger.LogInformation("Added:" + Environment.NewLine + device + Environment.NewLine);
            //};


            //usbEventWatcher.UsbDriveEjected += (_, path) => {
            //    _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            //    _logger.LogInformation("Ejected:" + Environment.NewLine + path + Environment.NewLine);
            //    };

            //CheckConnectedUsbDrives();

            _usbEventWatcher.UsbDriveMounted += (_, path) =>
            {
                CreateLogs(DateTime.Now.ToString() + " External device detected (Drive " + path + ")");

                ReadINIFiles(path, FileSource.USB);
            };

            CreateLogs($"{DateTime.Now} USB event watcher initialized and monitoring for device connections.");
        }

        void CheckConnectedUsbDrives()
        {
            // take all drive info
            var drives = DriveInfo.GetDrives();

            // Only USB drive (When DriveType is Removabl)
            var usbDrives = drives.Where(d => d.DriveType == DriveType.Removable);

            foreach (var drive in usbDrives)
            {
                string path = drive.RootDirectory.FullName;
                CreateLogs(DateTime.Now.ToString() + " Already connected USB drive detected (Drive " + path + ")");//display I drive
                ReadINIFiles(path, FileSource.USB);
            }
        }

        void CreateLogs(string Info)
        {
            var path = ManageFiles.LogsFolderPath;

            // Always use the correct log file name for today's date
            // This prevents accidentally writing to rotated backup files (.1.txt)
            string fileName = DateTime.Now.Date.ToString("MMddyyyy") + "_Logfile.txt";
            string logFilePath = Path.Combine(path, fileName);

            // Check if log file needs rotation (2MB = 2097152 bytes)
            const long MAX_LOG_SIZE = 2097152;
            try
            {
                if (File.Exists(logFilePath))
                {
                    FileInfo fileInfo = new FileInfo(logFilePath);
                    if (fileInfo.Length >= MAX_LOG_SIZE)
                    {
                        RotateLogFile(logFilePath);
                    }
                }
            }
            catch
            {
                // If rotation fails, continue writing to current log
            }

            ManageFiles.CreateAndAppendLogs(logFilePath, Info);
        }

        /// <summary>
        /// Rotates the log file by renaming it with .1 suffix and deleting any older .1 backup.
        /// Only keeps 1 rotated backup file.
        /// </summary>
        void RotateLogFile(string logFilePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(logFilePath) ?? ManageFiles.LogsFolderPath;
                string fileName = Path.GetFileNameWithoutExtension(logFilePath);
                string extension = Path.GetExtension(logFilePath);
                string rotatedFilePath = Path.Combine(directory, $"{fileName}.1{extension}");

                // Delete old backup if it exists (only keep 1 backup)
                if (File.Exists(rotatedFilePath))
                {
                    File.Delete(rotatedFilePath);
                }

                // Rename current log to .1
                File.Move(logFilePath, rotatedFilePath);

                // Log rotation in the new file
                string rotationMessage = $"{DateTime.Now} === Log file rotated (previous log saved as {Path.GetFileName(rotatedFilePath)}) ===";
                ManageFiles.CreateAndAppendLogs(logFilePath, rotationMessage);
            }
            catch (Exception ex)
            {
                // If rotation fails, log error in current file
                string errorMessage = $"{DateTime.Now} Error rotating log file: {ex.Message}";
                ManageFiles.CreateAndAppendLogs(logFilePath, errorMessage);
            }
        }

        /// <summary>
        /// Moves a file from the Working folder to the Processed folder after successful upload.
        /// Includes retry logic for file locking. If move fails, prepends MOVE_ERROR_ to filename.
        /// </summary>
        /// <param name="workingFilePath">Full path to the file in the Working folder.</param>
        /// <returns>True if move was successful, false otherwise.</returns>
        private bool MoveToProcessedFolder(string workingFilePath)
        {
            string fileName = Path.GetFileName(workingFilePath);
            string processedFilePath = Path.Combine(ManageFiles.ProcessedFolderPath, fileName);

            int maxRetries = 5;
            int delayMs = 500;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // Check if destination already exists
                    if (File.Exists(processedFilePath))
                    {
                        // Generate unique name
                        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                        string ext = Path.GetExtension(fileName);
                        int suffix = 1;
                        while (File.Exists(Path.Combine(ManageFiles.ProcessedFolderPath, $"{nameWithoutExt}_{suffix}{ext}")))
                        {
                            suffix++;
                        }
                        processedFilePath = Path.Combine(ManageFiles.ProcessedFolderPath, $"{nameWithoutExt}_{suffix}{ext}");
                    }

                    File.Move(workingFilePath, processedFilePath);
                    CreateLogs($"{DateTime.Now} File moved to Processed folder: {Path.GetFileName(processedFilePath)}");
                    return true;
                }
                catch (IOException ex) when (attempt < maxRetries)
                {
                    // File might be locked, wait and retry
                    if (attempt == 1)
                    {
                        CreateLogs($"{DateTime.Now} File locked, waiting to move to Processed folder (attempt {attempt}/{maxRetries}): {fileName}");
                    }
                    Thread.Sleep(delayMs);
                }
                catch (Exception ex)
                {
                    CreateLogs($"{DateTime.Now} ERROR: Failed to move file to Processed folder: {fileName}");
                    CreateLogs($"{DateTime.Now} Exception: {ex.GetType().Name} - {ex.Message}");

                    // Rename with MOVE_ERROR_ prefix and leave in Working folder
                    try
                    {
                        string errorFileName = $"MOVE_ERROR_{fileName}";
                        string errorFilePath = Path.Combine(ManageFiles.WorkingFolderPath, errorFileName);

                        if (File.Exists(errorFilePath))
                        {
                            // Add timestamp to make unique
                            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                            string ext = Path.GetExtension(fileName);
                            errorFileName = $"MOVE_ERROR_{nameWithoutExt}_{DateTime.Now:HHmmss}{ext}";
                            errorFilePath = Path.Combine(ManageFiles.WorkingFolderPath, errorFileName);
                        }

                        File.Move(workingFilePath, errorFilePath);
                        CreateLogs($"{DateTime.Now} File renamed to: {errorFileName} (will remain in Working folder)");
                    }
                    catch (Exception renameEx)
                    {
                        CreateLogs($"{DateTime.Now} WARNING: Could not rename file with MOVE_ERROR prefix: {renameEx.Message}");
                        CreateLogs($"{DateTime.Now} File remains as: {fileName} in Working folder");
                    }
                    return false;
                }
            }

            // Max retries exceeded
            CreateLogs($"{DateTime.Now} ERROR: Max retries exceeded trying to move file to Processed folder: {fileName}");
            try
            {
                string errorFileName = $"MOVE_ERROR_{fileName}";
                string errorFilePath = Path.Combine(ManageFiles.WorkingFolderPath, errorFileName);
                File.Move(workingFilePath, errorFilePath);
                CreateLogs($"{DateTime.Now} File renamed to: {errorFileName} (will remain in Working folder)");
            }
            catch
            {
                CreateLogs($"{DateTime.Now} File remains as: {fileName} in Working folder");
            }
            return false;
        }

        /// <summary>
        /// Logs an error to the central location (same folder as centralconfig.ini).
        /// Format: Timestamp | APP_Identifier | Error Type | Filename | Error Details
        /// This is non-blocking - if it fails, it logs locally and continues.
        /// </summary>
        /// <param name="errorType">Type of error (e.g., UPLOAD_ERROR, AUTHOR_MAPPING)</param>
        /// <param name="fileName">Name of the file that caused the error</param>
        /// <param name="errorDetails">Detailed error message</param>
        private void LogErrorToCentralLocation(string errorType, string fileName, string errorDetails)
        {
            try
            {
                // Get the directory of the central config file
                if (string.IsNullOrEmpty(LocalConfig.Central_Config_UNC))
                {
                    CreateLogs($"{DateTime.Now} Cannot log to central location: Central_Config_UNC not set");
                    return;
                }

                string centralConfigDir = Path.GetDirectoryName(LocalConfig.Central_Config_UNC);
                if (string.IsNullOrEmpty(centralConfigDir) || !Directory.Exists(centralConfigDir))
                {
                    CreateLogs($"{DateTime.Now} Cannot log to central location: Directory not accessible - {centralConfigDir}");
                    return;
                }

                // Create daily error log filename
                string errorLogFileName = $"Errors_{DateTime.Now:MMddyyyy}.log";
                string errorLogPath = Path.Combine(centralConfigDir, errorLogFileName);

                // Get workstation identifier
                string workstationId = string.IsNullOrEmpty(LocalConfig.APP_Identifier) ? Environment.MachineName : LocalConfig.APP_Identifier;

                // Format the error entry
                string errorEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {workstationId} | {errorType} | {fileName} | {errorDetails}";

                // Append to the central error log
                using (StreamWriter sw = File.AppendText(errorLogPath))
                {
                    sw.WriteLine(errorEntry);
                }

                CreateLogs($"{DateTime.Now} Error logged to central location: {errorLogFileName}");
            }
            catch (Exception ex)
            {
                // Non-blocking - just log locally and continue
                CreateLogs($"{DateTime.Now} WARNING: Failed to log error to central location: {ex.Message}");
                CreateLogs($"{DateTime.Now} Error details that could not be centralized: [{errorType}] {fileName} - {errorDetails}");
            }
        }
    }
}
