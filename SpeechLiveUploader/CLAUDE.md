# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**SpeechLive Upload Helper** is a Windows background service that monitors USB dictation devices and Import folder, extracts audio files (.ds2/.dss), reads embedded metadata, and uploads them to a SpeechLive API endpoint. The application runs as a Windows Service with no GUI, providing audio feedback for success/failure.

**Version:** 1.0.17

## Technology Stack

- .NET 8.0 Worker Service (C#)
- Windows Service integration via `Microsoft.Extensions.Hosting.WindowsServices`
- USB device detection via `Usb.Events` library
- Vitalytics remote monitoring for application health tracking (v1.0.11)
- Inno Setup for installer creation

## Build Commands

```bash
# Build the solution
dotnet build

# Run locally (for testing)
dotnet run

# Publish as single-file executable
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## Solution Architecture

This is a Visual Studio solution with 5 projects:

1. **SpeechLiveUploader** - Main Windows service application
2. **EncryptionLibrary** - Shared library for API key encryption/decryption using AES
3. **ApiKeyEncryptor** - WinForms utility for encrypting API keys before storing in config
4. **WindowsFormsApp1** - Legacy/utility project
5. **ConsoleApp1** - Legacy/utility project

**Dependency:** SpeechLiveUploader and ApiKeyEncryptor both reference EncryptionLibrary.

## Core Architecture

### Main Service Flow (Worker.cs)

The `Worker` class is a `BackgroundService` that:

1. **Monitors USB devices** using `IUsbEventWatcher` from Usb.Events library
2. **Monitors Import folder** using `FileSystemWatcher` for manually added files
3. **On USB mount event or Import folder file detection**, triggers file processing pipeline:
   - Load local config from `C:\ProgramData\SpeechLive Helper\localconfig.ini`
   - Load central config from UNC path specified in local config
   - Scan source (USB or Import) for `.ds2` and `.dss` files
   - Extract metadata from first 900 bytes of each file header
   - Map Author name to User ID via central config
   - Copy file to Working directory with timestamped filename (unique conflict resolution for Import files)
   - Upload to SpeechLive API with metadata and custom headers
   - On success:
     - Move file from Working folder to Processed folder (v1.0.9)
     - **USB files:** Optionally delete from USB based on config
     - **Import files:** Delete from Import folder
   - On failure:
     - Copy to Error folder, delete from Working folder
     - Log error to central location (v1.0.9)
     - Delete from Import (if Import source)
   - Play success or error sound

### Import Folder Feature (v1.0.4)

Enables manual file processing without USB device:
- **Location:** `C:\ProgramData\SpeechLive Helper\Import\`
- **Use cases:** Retry failed uploads, remote testing, manual file submission
- **Monitoring:** FileSystemWatcher detects new .ds2/.dss files in real-time
- **File lock handling:** Waits up to 10 seconds (20 retries @ 500ms) for files to be fully written
- **Conflict resolution:** Automatically renames files if same name exists in Working folder (appends _1, _2, etc.)
- **Cleanup:** Import folder is always cleared after processing (success or failure)
- **Startup check:** Processes any existing files in Import folder when service starts

### Configuration System

**Two-tier INI configuration:**

**Local Config** (`C:\ProgramData\SpeechLive Helper\localconfig.ini`):
```ini
[Config]
Central_Config_UNC=\\server\path\to\central.ini
APP_Identifier=LocationCode
Delete_Files_After_Upload=true
Vitalytics_Enabled=true
Vitalytics_DataMode=prod
```

**Central Config** (shared UNC path):
```ini
[Config]
API_Bearer=<encrypted_base64_token>
API_UserAgent=SpeechLiveHelper/1.2
API_Tenant=https://api.speechlive.com/v1
Vitalytics_ApiKey=<encrypted_vitalytics_token>
Vitalytics_ApiBaseUrl=https://api.vitalytics.app/api/v1

[Users]
AuthorName1=user-id-guid-1
AuthorName2=user-id-guid-2
```

This design allows central management of API credentials and user mappings while each workstation has its own location identifier.

### API Integration (ApiHelper.cs)

**Endpoint:** `POST {API_Tenant}/dictations`

**Request includes:**
- Multipart form data with audio file renamed as `{original}-[{APP_Identifier}].{ext}`
- Form fields: `priority` (byte 69 from file), `worktype` (from metadata), `custom5` (dictated date if present)
- Custom headers: Authorization Bearer token (decrypted at runtime), App-Name, App-Version, Device-Id, SL-User, User-Agent

**Key implementation detail:** The API helper extracts priority and dictated date by reading specific byte offsets from the file before upload (offset 69 for priority, offset 38-50 for dictated date in YYMMDDHHMMSS format).

### Metadata Extraction (ManageFiles.cs)

The `ExtractMetadata()` method reads the first 900 bytes of .ds2/.dss files to parse:
- **Author name** - Extracted via regex from hex-converted header
- **Work Type** - Case-insensitive match for "Work Type" or "Work type"
- **Device ID** - 12 digits following @ or % symbol

This metadata is embedded in the proprietary file format headers and is critical for routing uploads to the correct user.

### Encryption (EncryptionLibrary)

Uses AES-256 encryption with PBKDF2 key derivation (1000 iterations, SHA256). The secret key is `SpeechLiveSecretKey2024`. API tokens are stored encrypted (Base64) in central config and decrypted at runtime.

### Vitalytics Remote Monitoring (v1.0.11)

The `VitalyticsService` singleton provides non-blocking remote monitoring via the Vitalytics API. All operations are fire-and-forget and never affect main processing.

**Architecture:**
- `Services/VitalyticsService.cs` - Singleton service with event queuing (`ConcurrentQueue<VitalyticsEvent>`)
- `Models/VitalyticsModels.cs` - DTOs for API communication (`VitalyticsEvent`, `VitalyticsRequest`, `VitalyticsDeviceInfo`)

**Events Tracked:**
| Event | Level | Trigger |
|-------|-------|---------|
| Service startup | info | Service starts |
| Service shutdown | info | Service stops |
| Heartbeat | heartbeat | Every 10 minutes |
| Upload success | info | File uploaded successfully |
| Upload failure | error | API returns non-success status |
| Author mapping failure | crash | Author not found in config |
| Critical exception | crash | Unhandled exceptions |

**API Integration:**
- **Endpoint:** `POST {Vitalytics_ApiBaseUrl}/health/events`
- **Headers:** `X-API-Key` (decrypted), `X-App-Identifier` ("speechlive-windows")
- **Device ID:** Persisted GUID stored at `C:\ProgramData\SpeechLive Helper\vitalytics_device_id.txt`

**Configuration:**
- `Vitalytics_Enabled` (localconfig.ini) - "true" or "false", defaults to "true"
- `Vitalytics_DataMode` (localconfig.ini) - "test" or "prod", defaults to "prod"
- `Vitalytics_ApiKey` (centralconfig.ini) - Encrypted API key
- `Vitalytics_ApiBaseUrl` (centralconfig.ini) - API base URL

**Key Design Decisions:**
- Uses `SemaphoreSlim` for async-safe flush locking (not `Monitor` which is thread-specific)
- Immediate flush for errors/crashes, heartbeats, and upload events
- Re-queues events on server errors (5xx) but not on auth errors (4xx)
- Device model uses `APP_Identifier` from local config

## Key Directories

**Development:**
- Source: `C:\Users\Ryan G\Desktop\speechlive_helper\`
- Published output: `SpeechLiveUploader\bin\Release\net8.0\publish\win-x64\`

**Runtime (created automatically):**
- `C:\ProgramData\SpeechLive Helper\Working\` - Temporary staging area during upload
- `C:\ProgramData\SpeechLive Helper\Processed\` - Successfully uploaded files (v1.0.9)
- `C:\ProgramData\SpeechLive Helper\Error\` - Failed uploads
- `C:\ProgramData\SpeechLive Helper\Import\` - Drop files here for manual processing (v1.0.4)
- `C:\ProgramData\SpeechLive Helper\Logs\` - Log files with automatic 2MB rotation
- `C:\ProgramData\SpeechLive Helper\localconfig.ini` - Local configuration
- `C:\ProgramData\SpeechLive Helper\vitalytics_device_id.txt` - Persistent device ID for Vitalytics (v1.0.11)
- `C:\ProgramData\SpeechLive Helper\success.wav` / `error.wav` - Audio feedback files

**Installer build:**
- Source files: `C:\Users\Ryan G\Desktop\SpeechLiveHelper\InstallerFiles\`
- Inno Setup script: `speechlivehelper_prod.iss`
- Output: `C:\Users\Ryan G\Desktop\SpeechLiveHelper\Output\slhsetup.exe`

## Installer Creation Process

1. Publish SpeechLiveUploader project (right-click → Publish or use dotnet publish)
2. Copy published files to `C:\Users\Ryan G\Desktop\SpeechLiveHelper\InstallerFiles\`
3. Delete `.pdb` symbol files
4. Copy support files (success.wav, error.wav, icons) if not present
5. Clear Output folder
6. Open `speechlivehelper_prod.iss` in Inno Setup Compiler
7. Build (requires code signing certificate connected)

The installer creates a Windows Service named "SpeechLive Upload Helper" and installs to `%APPDATA%\SpeechLive Helper\`.

## Important Implementation Details

1. **Async file operations wrapped in Task.Run()** - All file I/O in Worker.cs is executed inside `Task.Run(async () => {...})` to avoid blocking the service thread.

2. **Static configuration classes** - `LocalConfig` and `CentralConfig` use static properties for global access throughout the application.

3. **Timestamped filenames** - All files are renamed to `{original}_{ddMMyy_HHmmss}.{ext}` before processing to ensure uniqueness.

4. **INI file reading via P/Invoke** - The `IniFile.cs` helper uses Windows kernel32.dll APIs (`GetPrivateProfileString`, `GetPrivateProfileSection`) rather than third-party libraries.

5. **No retry logic** - API failures result in immediate copy to Error folder without retry attempts.

6. **Audio feedback via PowerShell** - Since the service has no GUI, success/error sounds are played by spawning PowerShell processes with `-Command (New-Object Media.SoundPlayer 'path').PlaySync()`.

7. **Hard-coded ProgramData paths** - The paths in `ManageFiles.cs` are constants and not configurable through settings.

8. **USB event handler runs once per mount** - The ExecuteAsync method has commented-out loop logic; it processes files once when a USB device mounts, then waits.

9. **FileSource enum (v1.0.4)** - Distinguishes between USB and Import file sources to apply different cleanup logic (USB respects Delete_Files_After_Upload config, Import always deletes).

10. **FileSystemWatcher for Import folder (v1.0.4)** - Monitors `Created` and `Renamed` events, with file lock retry logic to ensure files are fully written before processing.

11. **Unique filename generation (v1.0.4)** - `GetUniqueWorkingFileName()` prevents conflicts by appending numeric suffix (_1, _2, etc.) when Import files have same name as existing Working folder files.

12. **Startup configuration verification (v1.0.4)** - `VerifyConfigurationOnStartup()` runs during service initialization to validate local and central config accessibility and log all settings.

13. **Log file rotation (v1.0.4, updated v1.0.8)** - `RotateLogFile()` automatically manages log file size by rotating at 2MB threshold, keeping only 1 backup copy. Fixed in v1.0.8 to always use the correct dated filename, preventing cascading rotation bug.

14. **Comprehensive exception logging (v1.0.5)** - All catch blocks log complete exception details including type, message, stack trace, and inner exceptions to aid troubleshooting.

15. **API call debug logging (v1.0.5)** - Logs detailed information before/after each API call including all parameters (AuthorId, WorkType, DeviceId), request endpoints, and response status codes.

16. **Default worktype handling (v1.0.5)** - ApiHelper automatically sets "OFFICE VISIT" as default worktype when empty, with logging notification.

17. **File deletion error handling (v1.0.5)** - File deletion failures are caught and logged as warnings but don't fail the entire upload operation. Automatically clears ReadOnly attribute before deletion attempts to prevent permission errors.

18. **Service identity logging (v1.0.5)** - Logs Windows identity of the service account during file operations to aid permission troubleshooting.

19. **Heartbeat timer (v1.0.7, updated v1.0.11)** - A System.Threading.Timer logs a heartbeat message every 10 minutes (changed from 5 minutes in v1.0.11) to confirm the service is running and actively monitoring. Also sends heartbeat to Vitalytics for remote monitoring. Initialized during Worker constructor startup.

20. **Processed folder (v1.0.9)** - After successful API upload, files are moved from Working folder to Processed folder. Working folder is now a temporary staging area. Move includes retry logic for file locking (5 attempts, 500ms delay). If move fails, file is renamed with `MOVE_ERROR_` prefix and left in Working folder.

21. **Centralized error logging (v1.0.9)** - Errors are logged to a shared location (same folder as `centralconfig.ini`) in daily log files (`Errors_MMddyyyy.log`). Format: `Timestamp | APP_Identifier | Error Type | Filename | Error Details`. This is non-blocking - if the shared folder is inaccessible, errors are logged locally and processing continues without interruption.

22. **Thread-safe Import folder processing (v1.0.10)** - Uses `_processingLock` object and `isProcessing`/`_pendingImportCheck` flags to prevent race conditions when multiple files are copied to the Import folder simultaneously. The `ClearImportProcessingFlag()` helper ensures proper cleanup at all early exit points. Only affects Import folder processing - USB device processing is unaffected.

23. **Vitalytics remote monitoring (v1.0.11)** - `VitalyticsService` singleton tracks application health events and sends them to Vitalytics API. Uses `SemaphoreSlim` for async-safe flush locking (not `Monitor` which fails across `await` boundaries). Events are queued in `ConcurrentQueue<VitalyticsEvent>` and flushed immediately for errors/crashes/heartbeats or batched (10 events) for info-level events. Completely non-blocking - Vitalytics failures never affect main processing. Device ID is persisted to file for consistent identification across service restarts.

24. **USB disconnection graceful handling (v1.0.12)** - `IsUsbDisconnectionError()` helper method detects when USB device is disconnected during file processing. Checks for `DirectoryNotFoundException`, `DriveNotFoundException`, and specific `IOException` messages. When detected, logs WARNING and skips remaining files instead of reporting each as a crash.

25. **Log retention with archiving (v1.0.13)** - `PerformLogMaintenance()` runs on service startup to archive previous days' logs to `Logs\Archive\` and purge logs older than `Log_Retention_Days` (default 30). Reports maintenance stats to Vitalytics.

26. **Shutdown diagnostics (v1.0.14)** - `ShutdownDiagnostics` class captures unhandled exceptions, process exit events, and unobserved task exceptions. Writes to `shutdown_events.log` and creates `diagnostic_report.txt`. Classifies shutdowns as graceful or crash for Vitalytics alerting. UsbEventWatcher stored as class field to prevent garbage collection.

27. **Duplicate file upload prevention (v1.0.15)** - `_recentlyProcessedFiles` dictionary tracks successfully uploaded files for 5 minutes. `WasFileRecentlyProcessed()` and `MarkFileAsProcessed()` prevent duplicate uploads when USB is disconnected and reconnected quickly before file deletion completes.

28. **USB disconnection during file enumeration (v1.0.16, improved v1.0.17)** - Initial `GetDs2Files()` and `GetDsFiles()` calls wrapped in try-catch to handle USB disconnection during `Directory.GetFiles()`. Added "parameter is incorrect" to `IsUsbDisconnectionError()` detection. In v1.0.17, replaced `when` exception filter with standard catch to prevent uncaught exceptions in `async void` method, and added `DriveInfo.IsReady` pre-check before enumeration.

## Version History

### v1.0.17 (Current)
**Robust USB Enumeration Error Handling**

**Bug Fixes:**
- **Fixed service crash on USB remount after disconnection:** The v1.0.16 `when` exception filter on the catch clause could allow `IOException` to escape uncaught from the `async void ReadINIFiles()` method, crashing the service via `AppDomain.UnhandledException`. Replaced with standard catch that handles all exceptions during file enumeration gracefully.
- **Added drive readiness pre-check:** Before attempting `Directory.GetFiles()` on a USB drive, checks `DriveInfo.IsReady` to detect drives that have mounted at the OS level but whose filesystem is not yet stable (common during quick disconnect/reconnect cycles).

**Technical Details:**
- Removed `catch (Exception ex) when (source == FileSource.USB && IsUsbDisconnectionError(ex))` pattern — if the `when` filter returned `false` for any reason, the exception propagated uncaught out of the `async void` method, terminating the process
- Replaced with `catch (Exception ex)` that checks `IsUsbDisconnectionError()` inside the catch body, with a fallback branch that logs and reports unexpected enumeration errors to Vitalytics
- Added `DriveInfo.IsReady` check with its own try-catch guard before file enumeration

### v1.0.16
**USB Disconnection During File Enumeration Fix**

**Bug Fixes:**
- **Fixed crash when USB disconnects during initial file scan:** Previously, if a USB device was disconnected while `Directory.GetFiles()` was enumerating files, an unhandled `IOException: The parameter is incorrect` would crash the service. The service would auto-restart via Windows SCM, but this created unnecessary downtime and Vitalytics crash alerts.
- **Extended `IsUsbDisconnectionError()` detection:** Added "parameter is incorrect" to the list of recognized USB disconnection error messages, alongside existing checks for "device is not ready" and "not find a part of the path".

**Technical Details:**
- Wrapped initial `GetDs2Files()` and `GetDsFiles()` calls in try-catch with `when (source == FileSource.USB && IsUsbDisconnectionError(ex))` filter
- On USB disconnection during enumeration, logs WARNING and exits gracefully instead of crashing
- Made `filePath` parameter optional in `IsUsbDisconnectionError()` since it's only used for logging context
- Case-insensitive message matching using `ToLower()` for more robust detection

### v1.0.15
**Duplicate File Upload Prevention**

**New Features:**
- **Recently Processed File Tracking:** Tracks successfully uploaded files in memory for 5 minutes to prevent duplicate uploads
- **USB Reconnection Race Condition:** Handles scenario where USB is disconnected and reconnected quickly before file deletion completes on the device
- **Duplicate Prevention Logging:** Logs when files are skipped with "duplicate prevention" message

**Technical Details:**
- `_recentlyProcessedFiles` dictionary stores filename → DateTime of last successful upload
- `WasFileRecentlyProcessed()` checks if file was uploaded within `DUPLICATE_PREVENTION_MINUTES` (5 minutes)
- `MarkFileAsProcessed()` called after successful API upload
- Automatic cleanup of expired entries on each check
- Thread-safe using `_recentFilesLock` object

### v1.0.14
**Shutdown Diagnostics & Service Stability**

**Bug Fixes:**
- **Fixed UsbEventWatcher garbage collection:** Stored `IUsbEventWatcher` as class field `_usbEventWatcher` to prevent premature garbage collection that could stop USB monitoring
- **Fixed ExecuteAsync loop:** Changed from one-shot execution to proper monitoring loop that keeps service running until cancellation

**New Features:**
- **ShutdownDiagnostics class:** Comprehensive crash detection and logging system
- **Unhandled Exception Capture:** `AppDomain.CurrentDomain.UnhandledException` handler logs full details before crash
- **Process Exit Tracking:** Captures exit codes and determines if shutdown was graceful or unexpected
- **Task Exception Handling:** `TaskScheduler.UnobservedTaskException` handler prevents silent async failures
- **Shutdown Classification:** Distinguishes between graceful shutdowns (manual stop, system shutdown) and crashes
- **Diagnostic Reports:** `shutdown_events.log` and `diagnostic_report.txt` for remote debugging
- **Windows Event Log Query:** Retrieves recent service-related events on shutdown

**New Files:**
- `Helpers/ShutdownDiagnostics.cs` - Centralized shutdown diagnostics

**Vitalytics Integration:**
- Crash-level events sent to Vitalytics for unexpected terminations
- `TrackUnexpectedTermination()` method for crash reporting

### v1.0.13
**Log Retention with Archiving**

**New Features:**
- **Log Archive Folder:** Previous days' logs automatically moved to `Logs\Archive\` subfolder
- **Configurable Retention:** `Log_Retention_Days` setting in localconfig.ini (default 30 days)
- **Automatic Purging:** Archived logs older than retention period are deleted on service startup
- **Maintenance Tracking:** Logs files archived, files purged, and bytes freed; sends to Vitalytics

**Configuration Changes:**
- Added `Log_Retention_Days` to LocalConfig (default: "30")
- Added `LogsArchiveFolderPath` constant to ManageFiles

**New Methods:**
- `PerformLogMaintenance()` - Called on service startup, handles archive and purge
- `VitalyticsService.TrackLogMaintenance()` - Reports maintenance stats to Vitalytics

### v1.0.12
**USB Disconnection Graceful Handling (Per-File)**

**Bug Fixes:**
- **Graceful USB disconnection during file processing:** Previously, if a USB device was unplugged during the file processing loop, each remaining file triggered a critical exception reported to Vitalytics as a crash. Now detected and handled gracefully.

**New Features:**
- **`IsUsbDisconnectionError()` helper:** Detects `DirectoryNotFoundException`, `DriveNotFoundException`, and `IOException` with device-not-ready messages
- **Pre-file existence check:** `File.Exists()` check before processing each USB file
- **Early exit on disconnection:** Logs WARNING and skips remaining files instead of logging ERROR per file
- **DSS processing skip:** If USB disconnection detected during DS2 processing, DSS processing is skipped entirely

**Technical Details:**
- `usbDisconnected` flag tracks disconnection state across file types
- Only applies to `FileSource.USB` - Import folder processing unaffected

### v1.0.11
**Vitalytics Remote Monitoring Integration**

**New Features:**
- **Vitalytics Integration:** Added remote monitoring via Vitalytics API to track application health, errors, and usage patterns across all installations
- **Event Tracking:** Service startup/shutdown, heartbeat (every 10 minutes), upload success/failure, author mapping failures, and critical exceptions
- **Persistent Device ID:** Each installation generates a unique device ID stored in `vitalytics_device_id.txt` for consistent identification
- **Configurable Data Mode:** Support for test/production data modes via `Vitalytics_DataMode` setting

**New Files:**
- `Services/VitalyticsService.cs` - Singleton service with non-blocking event queuing and HTTP posting
- `Models/VitalyticsModels.cs` - DTOs for Vitalytics API communication

**Configuration Changes:**
- Added `Vitalytics_Enabled` and `Vitalytics_DataMode` to LocalConfig
- Added `Vitalytics_ApiKey` and `Vitalytics_ApiBaseUrl` to CentralConfig

**Other Changes:**
- Heartbeat interval changed from 5 minutes to 10 minutes
- Added `StopAsync()` override in Worker.cs to track shutdown and flush events

**Technical Details:**
- Uses `SemaphoreSlim` for async-safe flush locking (fixes issue where `Monitor` fails across `await` boundaries)
- Events are queued in `ConcurrentQueue<VitalyticsEvent>` and flushed immediately for errors/crashes or batched for info events
- Re-queues events on server errors (5xx) but discards on auth errors (4xx) to prevent infinite growth
- All Vitalytics operations are non-blocking and never affect main file processing

### v1.0.10
**Import Folder Race Condition Fix**

**Bug Fixes:**
- **Fixed race condition when copying multiple files to Import folder:** When multiple files were copied simultaneously, `FileSystemWatcher` fired separate events for each file. Each event called `ReadINIFiles()` which processed ALL files in the folder, causing duplicate processing, delete errors, and files getting stuck.
- **Fixed `isProcessing` flag never being cleared:** When a recheck found no files and returned early, the `isProcessing` flag remained `true` forever, blocking all future Import folder processing.

**New Features:**
- **Thread-safe locking mechanism:** Added `_processingLock` object for proper thread synchronization
- **Pending recheck flag:** Added `_pendingImportCheck` flag to track files that arrive during processing and trigger a recheck after current batch completes
- **`ClearImportProcessingFlag()` helper:** New method ensures proper flag cleanup before all early returns in `ReadINIFiles()`

**Technical Details:**
- When `isProcessing` is true, new file events set `_pendingImportCheck = true` and return immediately
- After processing completes, if `_pendingImportCheck` is true, the Import folder is rescanned
- All early exit paths in `ReadINIFiles()` now call `ClearImportProcessingFlag()` to prevent stuck states
- USB device processing is completely unaffected - the locking only applies when `source == FileSource.Import`

### v1.0.9
**Processed Folder & Centralized Error Logging**

**New Features:**
- **Processed Folder:** Successfully uploaded files are now moved from the Working folder to a new `Processed` folder (`C:\ProgramData\SpeechLive Helper\Processed\`). This provides clearer organization - Working is now a temporary staging area, Processed contains confirmed successful uploads.
- **Centralized Error Logging:** Errors are now logged to a shared location (same folder as `centralconfig.ini`) for easier multi-station monitoring. Daily log files (`Errors_MMddyyyy.log`) contain entries in format: `Timestamp | APP_Identifier | Error Type | Filename | Error Details`
- **Error Types Logged Centrally:**
  - `UPLOAD_ERROR` - API upload failures with status code and response
  - `AUTHOR_MAPPING` - Author not found in central config user mappings

**New Methods:**
- `MoveToProcessedFolder()` - Moves files from Working to Processed with retry logic for file locking. If move fails, prepends `MOVE_ERROR_` to filename and leaves in Working folder.
- `LogErrorToCentralLocation()` - Non-blocking error logging to shared folder. Falls back to local logging if shared location is inaccessible.

**Design Notes:**
- Central error logging is completely non-blocking - if the shared folder is inaccessible, errors are logged locally and processing continues
- Move failures don't affect the success status of uploads - files are already uploaded successfully

### v1.0.8
**Log Rotation Bug Fix**

**Bug Fixes:**
- **Fixed cascading log rotation bug:** The `CreateLogs` method was picking an arbitrary `.txt` file from the logs folder, which could include rotated backup files (`.1.txt`). When these backups reached the size threshold, they would rotate again creating filenames like `Logfile.1.1.txt`, `Logfile.1.1.1.txt`, etc., eventually exceeding Windows' 260-character path limit and becoming undeletable. Fixed by always generating the correct dated filename (`MMddyyyy_Logfile.txt`) instead of scanning for existing files.

**Changes:**
- Increased log rotation threshold from 1MB to 2MB for less frequent rotations
- `CreateLogs` now always uses the correctly dated filename pattern

### v1.0.7
**Heartbeat Monitoring & Improved USB Logging**

**New Features:**
- **Heartbeat Logging:** Service now logs a heartbeat message every 5 minutes (changed to 10 minutes in v1.0.11) showing it's actively monitoring USB devices and Import folder
- **Improved No-Files Logging:** When a USB device is connected with no supported files, logs now explicitly state "No supported files (.ds2 or .dss) found on USB device" instead of separate messages for each format
- **Early Exit Optimization:** Processing exits immediately when no files are found, avoiding unnecessary configuration loading

### v1.0.6
**USB Deletion Error Handling & Default Author Fallback**

### v1.0.5
**Enhanced Diagnostics & Error Handling**

**New Features:**
- **Comprehensive Exception Logging:** All catch blocks now log full exception details including type, message, stack trace, and inner exceptions
- **API Call Debug Logging:** Detailed logging before/after API calls with parameters, endpoints, headers, and response status codes
- **Metadata Validation Warnings:** Logs warnings for empty or missing Author, WorkType, or DeviceId fields
- **Default WorkType:** Automatically sets "OFFICE VISIT" as worktype if empty, with logging
- **File Deletion Diagnostics:** Logs service account identity, file attributes, and read-only status before deletion attempts
- **Non-Fatal Deletion Errors:** File deletion failures no longer cause upload operations to fail; logged as warnings instead
- **ReadOnly Attribute Handling:** Automatically clears ReadOnly attribute before file deletion to prevent permission errors

**Bug Fixes:**
- Fixed silent exception swallowing in catch blocks that prevented proper error diagnosis
- Fixed UnauthorizedAccessException when deleting Import folder files with restricted attributes

### v1.0.4
**Import Folder & Enhanced Logging**
- Import folder functionality for manual file processing
- Enhanced logging with file sizes, batch summaries, and retry details
- Startup configuration verification
- Automatic log file rotation at 2MB (updated in v1.0.8)
- FileSource enum and unique filename generation

## Version Management

- Assembly version is set in `SpeechLiveUploader.csproj` (currently 1.0.11)
- Installer automatically extracts version from compiled executable
- Update version in csproj before building for release

## Logging (Enhanced in v1.0.4, v1.0.5)

### Log Files

Log files are stored at `C:\ProgramData\SpeechLive Helper\Logs\` with the naming format `{MMddyyyy}_Logfile.txt`.

**Automatic Log Rotation:**
- Rotates when log file reaches 2MB (2,097,152 bytes)
- Renames current log to `{filename}.1.txt`
- Deletes old backup (only keeps 1 backup file)
- Logs rotation event in new file
- Always uses correctly dated filename to prevent cascading rotation bug (v1.0.8)

### Startup Configuration Verification (v1.0.4)

On service startup, the application verifies and logs:
- Local configuration file existence and path
- Local config values (Central UNC, APP Identifier, Delete After Upload setting)
- Central configuration file accessibility (tests network path and permissions)
- API Bearer token presence (confirms without logging actual token for security)
- API Tenant URL and User Agent
- Number of user mappings loaded
- Detailed error messages and troubleshooting hints if issues found

### Enhanced Logging Features (v1.0.4)

**File Processing Logs:**
- File sizes in human-readable format (bytes/KB/MB) when detected
- Processing batch summaries with file counts by type (.ds2/.dss)
- Success/failure counts and total processing duration
- Source type identification (USB device vs Import folder)

**File Lock Retry Logging:**
- Logs first retry attempt and every 5th attempt
- Shows attempt number and total attempts (e.g., "attempt 5/20")
- Logs successful availability after retries

**Configuration Details:**
- Number of user mappings loaded from central config
- API Tenant, User Agent, and APP Identifier values
- Logged both on startup and during each processing run

**Standard Event Logging:**
- USB device detection and mount events
- Import folder file detection
- Configuration file loading
- File processing steps (copy, upload, delete)
- Complete API responses (status codes and content)
- Errors and exceptions with full details
- Filename conflict resolution (when Import files renamed)

Thread-safe logging is implemented via `ManageFiles.CreateAndAppendLogs()`.

### Debug & Diagnostic Logging (v1.0.5)

**Exception Details:**
- Exception type, message, and full stack trace for all caught exceptions
- Inner exception details when present
- Context information (file path, operation being performed)

**API Call Diagnostics:**
- Pre-call logging with all parameters (AuthorId, WorkType, DeviceId, FilePath)
- Request endpoint URL and headers (SL-User, Device-Id)
- Form data contents (Priority, WorkType, DictatedDate)
- Post-call response status codes
- Success/failure outcomes with full API response content

**Metadata Validation:**
- Warnings for empty or missing Author fields
- Warnings for empty WorkType fields (before applying default)
- Warnings for missing or "Unknown" DeviceId values

**File Deletion Diagnostics:**
- Service account identity (WindowsIdentity.GetCurrent().Name)
- File existence verification
- File attributes (ReadOnly, Hidden, System, etc.)
- ReadOnly attribute clearing operations
- Deletion failure warnings with exception details

**Default Value Logging:**
- Logs when default "OFFICE VISIT" worktype is applied to empty fields

## Testing

No automated tests exist in this solution. Testing is performed manually by:

**USB Testing:**
1. Running the service locally with `dotnet run`
2. Connecting a USB device with test .ds2/.dss files
3. Monitoring logs for proper processing
4. Verifying API uploads and error handling

**Import Folder Testing (v1.0.4):**
1. Running the service locally with `dotnet run`
2. Copying test .ds2/.dss files to `C:\ProgramData\SpeechLive Helper\Import\`
3. Monitoring real-time detection and processing in logs
4. Testing retry scenarios by copying files from Error folder to Import
5. Verifying filename conflict resolution
6. Testing remote file submission via network share

### Centralized Error Logging (v1.0.9)

Errors from all stations are logged to a shared location for easier monitoring across multiple installations.

**Location:** Same folder as `centralconfig.ini` (e.g., `\\server\share\Errors_MMddyyyy.log`)

**Format:**
```
Timestamp | APP_Identifier | Error Type | Filename | Error Details
2024-12-04 10:30:15 | CLINIC-A | UPLOAD_ERROR | dictation.ds2 | StatusCode: 401 (Unauthorized), Response: ...
2024-12-04 10:32:00 | CLINIC-B | AUTHOR_MAPPING | note.dss | Author 'DrSmith' not found in central config user mappings
```

**Error Types:**
- `UPLOAD_ERROR` - API upload failures with status code and response content
- `AUTHOR_MAPPING` - Author name not found in central config [Users] section

**Design:**
- Non-blocking: If shared folder is inaccessible, errors are logged locally and processing continues
- Daily rotation: New log file created each day
- Uses `APP_Identifier` from localconfig.ini to identify the workstation

## Common Development Patterns

- **Error-first logging**: Always log full error details including API response content
- **Graceful degradation**: Continue processing remaining files if one fails
- **Configuration validation**: Check for missing config files/values before processing
- **File safety**: Copy files to Working directory before upload, only delete source on confirmed success
- **Non-blocking operations**: Centralized error logging, Vitalytics monitoring, and file moves don't interrupt the main processing flow
- **Fire-and-forget async**: Use `_ = FlushAsync()` pattern for operations that shouldn't block (Vitalytics events)
- **Async-safe locking**: Use `SemaphoreSlim` instead of `Monitor` when locks span `await` boundaries
