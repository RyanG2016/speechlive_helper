# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**SpeechLive Upload Helper** is a Windows background service that monitors USB dictation devices, extracts audio files (.ds2/.dss), reads embedded metadata, and uploads them to a SpeechLive API endpoint. The application runs as a Windows Service with no GUI, providing audio feedback for success/failure.

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
2. **On USB mount event**, triggers file processing pipeline:
   - Load local config from `C:\ProgramData\SpeechLive Helper\localconfig.ini`
   - Load central config from UNC path specified in local config
   - Scan USB drive for `.ds2` and `.dss` files
   - Extract metadata from first 900 bytes of each file header
   - Map Author name to User ID via central config
   - Copy file to Working directory with timestamped filename
   - Upload to SpeechLive API with metadata and custom headers
   - On success: optionally delete from USB, play success sound
   - On failure: copy to Error folder, play error sound

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
- `C:\ProgramData\SpeechLive Helper\Working\` - Temporary storage during processing
- `C:\ProgramData\SpeechLive Helper\Error\` - Failed uploads
- `C:\ProgramData\SpeechLive Helper\Logs\` - Daily logs named `{MMddyyyy}_Logfile.txt`
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

## Version Management

- Assembly version is set in `SpeechLiveUploader.csproj` (currently 1.2)
- Installer automatically extracts version from compiled executable
- Update version in csproj before building for release

## Logging

All events are logged to daily files at `C:\ProgramData\SpeechLive Helper\Logs\{MMddyyyy}_Logfile.txt` including:
- USB device detection
- Configuration loading
- File processing steps
- Complete API responses (status codes and content)
- Errors and exceptions

Thread-safe logging is implemented via `ManageFiles.CreateAndAppendLogs()`.

## Testing

No automated tests exist in this solution. Testing is performed manually by:
1. Running the service locally with `dotnet run`
2. Connecting a USB device with test .ds2/.dss files
3. Monitoring logs for proper processing
4. Verifying API uploads and error handling

## Common Development Patterns

- **Error-first logging**: Always log full error details including API response content
- **Graceful degradation**: Continue processing remaining files if one fails
- **Configuration validation**: Check for missing config files/values before processing
- **File safety**: Copy files to Working directory before upload, only delete source on confirmed success
