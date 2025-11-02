# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**SpeechLive Upload Helper** is a Windows background service that monitors USB dictation devices and Import folder, extracts audio files (.ds2/.dss), reads embedded metadata, and uploads them to a SpeechLive API endpoint. The application runs as a Windows Service with no GUI, providing audio feedback for success/failure.

**Version:** 1.0.4

## Technology Stack

- .NET 8.0 Worker Service (C#)
- Windows Service integration via `Microsoft.Extensions.Hosting.WindowsServices`
- USB device detection via `Usb.Events` library
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
     - **USB files:** Optionally delete from USB based on config, copy to Working folder
     - **Import files:** Delete from Import folder (already in Working folder)
   - On failure: copy to Error folder, delete from Import (if Import source)
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
```

**Central Config** (shared UNC path):
```ini
[Config]
API_Bearer=<encrypted_base64_token>
API_UserAgent=SpeechLiveHelper/1.2
API_Tenant=https://api.speechlive.com/v1

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

## Key Directories

**Development:**
- Source: `C:\Users\Ryan G\Desktop\speechlive_helper\`
- Published output: `SpeechLiveUploader\bin\Release\net8.0\publish\win-x64\`

**Runtime (created automatically):**
- `C:\ProgramData\SpeechLive Helper\Working\` - Successfully processed files
- `C:\ProgramData\SpeechLive Helper\Error\` - Failed uploads
- `C:\ProgramData\SpeechLive Helper\Import\` - Drop files here for manual processing (v1.0.4)
- `C:\ProgramData\SpeechLive Helper\Logs\` - Log files with automatic 1MB rotation
- `C:\ProgramData\SpeechLive Helper\localconfig.ini` - Local configuration
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

13. **Log file rotation (v1.0.4)** - `RotateLogFile()` automatically manages log file size by rotating at 1MB threshold, keeping only 1 backup copy.

## Version Management

- Assembly version is set in `SpeechLiveUploader.csproj` (currently 1.0.4)
- Installer automatically extracts version from compiled executable
- Update version in csproj before building for release

## Logging (Enhanced in v1.0.4)

### Log Files

Log files are stored at `C:\ProgramData\SpeechLive Helper\Logs\` with the naming format `{MMddyyyy}_Logfile.txt`.

**Automatic Log Rotation:**
- Rotates when log file reaches 1MB (1,048,576 bytes)
- Renames current log to `{filename}.1.txt`
- Deletes old backup (only keeps 1 backup file)
- Logs rotation event in new file

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

## Common Development Patterns

- **Error-first logging**: Always log full error details including API response content
- **Graceful degradation**: Continue processing remaining files if one fails
- **Configuration validation**: Check for missing config files/values before processing
- **File safety**: Copy files to Working directory before upload, only delete source on confirmed success
