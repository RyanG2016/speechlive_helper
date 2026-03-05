# SpeechLive Upload Helper

A Windows background service that monitors USB dictation devices and automatically uploads audio files (.ds2/.dss) to the Philips® SpeechLive transcription platform.

> **Disclaimer**: This is an independent, open-source project and is **not** an official Philips® product. It is not affiliated with, endorsed by, or supported by Philips or Philips Speech. Use at your own risk.

> **Note**: Documentation is actively being improved. If you find any gaps or have questions, please open an issue.

## Why This App?

The native Philips® SpeechLive desktop uploader has limitations:
- **Requires a logged-in user** - Not suitable for central upload workstations where multiple physicians dock their devices
- **No DPM9600 support** - Philips does not support the DPM9600 in their native SpeechLive uploader

SpeechLive Upload Helper solves these issues by:
- Running as a background Windows Service (no user login required)
- Reading the author name embedded in each audio file's metadata
- Automatically mapping authors to their Philips® SpeechLive user accounts via configuration
- Uploading files on behalf of the correct user without manual intervention

This enables a "dock and walk away" workflow for shared workstations.

**Centralized Management**: Multiple installations across your organization can share a single central configuration file on a network share. Update API credentials or user mappings in one place and all workstations pick up the changes automatically.

**Secure Credentials**: The Philips® SpeechLive API key is stored encrypted (AES-256) in the central configuration file, so credentials are not exposed in plain text on shared network locations.

## Overview

SpeechLive Upload Helper runs as a Windows Service with no GUI. It detects USB dictation devices, extracts embedded metadata from audio files, maps authors to user IDs, and uploads files to the Philips® SpeechLive API. Audio feedback (success/error sounds) indicates upload status.

## Features

- **USB Device Monitoring**: Automatically detects connected USB dictation devices
- **Import Folder**: Drop files into a local folder for manual processing
- **Metadata Extraction**: Reads author, work type, priority status, and device ID from file headers
- **Priority Support**: Priority status set on the dictation device is preserved and uploaded to SpeechLive
- **Encrypted Credentials**: API tokens stored encrypted in configuration files
- **Remote Monitoring**: Optional integration with Vitalytics (see below)
- **Audio Feedback**: Plays sounds on upload success/failure
- **Automatic Log Rotation**: Manages log file sizes with archiving

### Supported Devices

Compatible with Philips dictation devices including (but not limited to):
- DPM7000
- DPM8000 / DPM8100
- DPM9600 / DPM9610

## Requirements

- **Philips® SpeechLive Enterprise Account** - API access is required, which is only available with an enterprise subscription
- Windows 10/11 or Windows Server 2016+
- .NET 8.0 Runtime
- Network access to Philips® SpeechLive API endpoints

## Quick Start

1. **Download** `slhsetup.exe` and `ApiKeyEncryptor.exe` from [GitHub Releases](https://github.com/RyanG2016/speechlive_helper/releases)

2. **Generate encryption key**
   - Run `ApiKeyEncryptor.exe`
   - Click **"Generate New Encryption Key"**
   - Copy the generated `secrets.json` to `C:\ProgramData\SpeechLive Helper\`

3. **Encrypt your API key**
   - In ApiKeyEncryptor, paste your SpeechLive API bearer token
   - Click **"Encrypt API Key"**
   - Copy the encrypted result

4. **Create centralconfig.ini** (on network share or locally):
   ```ini
   [Config]
   API_Bearer=<paste_encrypted_key_here>
   API_Tenant=https://sl-api-gateway-apim-prod-cae.azure-api.net
   API_UserAgent=SpeechLive Helper

   [Users]
   7777=<speechlive_author_email>
   ```

5. **Install the service**
   - Run `slhsetup.exe` as Administrator

6. **Create localconfig.ini** at `C:\ProgramData\SpeechLive Helper\`:
   ```ini
   [Config]
   Central_Config_UNC=\\server\share\centralconfig.ini
   APP_Identifier=WORKSTATION-01
   Delete_Files_After_Upload=true
   ```

7. **Start the service**
   ```cmd
   sc start "SpeechLive Upload Helper"
   ```

8. **Test** - Plug in a USB dictation device with .ds2/.dss files. You should hear a success sound when files upload.

---

## Installation

### Download

Download the latest release from the [GitHub Releases page](https://github.com/RyanG2016/speechlive_helper/releases):

- **slhsetup.exe** - Windows installer
- **ApiKeyEncryptor.zip** - Tool for encrypting API keys (optional, for setting up new encryption keys)

### Install

1. Run `slhsetup.exe` as Administrator
2. Configure `localconfig.ini` with your settings (see Configuration section)
3. Set up `centralconfig.ini` on your shared network location
4. Copy `secrets.json` to `C:\ProgramData\SpeechLive Helper\`
5. Start the "SpeechLive Upload Helper" Windows Service

## Configuration

The application uses a two-tier configuration system:
- **Local configuration** (`localconfig.ini`) - Machine-specific settings stored on each workstation
- **Central configuration** (`centralconfig.ini`) - Shared settings stored on a network share (or locally for single-machine deployments)

### Local Configuration (localconfig.ini)

Located at `C:\ProgramData\SpeechLive Helper\localconfig.ini`

```ini
[Config]
Central_Config_UNC=\\server\share\centralconfig.ini
APP_Identifier=CLINIC-A
Drive_Letter_To_Monitor=none
Delete_Files_After_Upload=true
Vitalytics_Enabled=false
Vitalytics_DataMode=prod
Log_Retention_Days=30
DEFAULT_AUTHOR_ID=
ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID=false
```

| Setting | Required | Description |
|---------|----------|-------------|
| `Central_Config_UNC` | Yes | Path to the central configuration file. Can be a UNC path (`\\server\share\centralconfig.ini`) or local path (`C:\ProgramData\SpeechLive Helper\centralconfig.ini`) |
| `APP_Identifier` | Yes | Unique identifier for this workstation. Used in logs and filenames to identify the source machine (e.g., `CLINIC-A`, `FRONT-DESK-01`) |
| `Drive_Letter_To_Monitor` | No | Specific drive letter to monitor (e.g., `E`). Set to `none` to auto-detect USB devices. Default: `none` |
| `Delete_Files_After_Upload` | Yes | Whether to delete files from the USB device after successful upload. Values: `true` or `false` |
| `Vitalytics_Enabled` | No | Enable remote monitoring via Vitalytics. Values: `true` or `false`. Default: `false` |
| `Vitalytics_DataMode` | No | Vitalytics environment mode. Values: `test` or `prod`. Default: `prod` |
| `Log_Retention_Days` | No | Number of days to retain archived log files before deletion. Default: `30` |
| `DEFAULT_AUTHOR_ID` | No | Fallback author name to use when the file's embedded author is not found in the user mappings |
| `ALWAYS_FALLBACK_TO_DEFAULT_AUTHORID` | No | If `true`, always use `DEFAULT_AUTHOR_ID` regardless of the file's embedded author. Values: `true` or `false`. Default: `false` |

### Central Configuration (centralconfig.ini)

Shared network location specified by `Central_Config_UNC`:

```ini
;Central Configuration File
;API_Bearer should contain an encrypted API key generated by the ApiKeyEncryptor tool

[Config]
API_Bearer=<encrypted_api_token>
API_Tenant=https://sl-api-gateway-apim-prod-cae.azure-api.net
API_UserAgent=SpeechLive Helper

;Optional: Vitalytics remote monitoring (only needed if Vitalytics_Enabled=true in localconfig.ini)
;Vitalytics_ApiKey=<encrypted_vitalytics_api_key>
;Vitalytics_ApiBaseUrl=https://api.vitalytics.app/api/v1

[Users]
;Format: AuthorID=SpeechLiveUserID
;AuthorID = The numeric author ID configured on the recording device
;SpeechLiveUserID = The user's SpeechLive account identifier (email or GUID from SpeechLive admin portal)
7777=drsmith@clinic.com
6666=drjones@clinic.com
5555=nurse01@clinic.com
```

#### [Config] Section

| Setting | Required | Description |
|---------|----------|-------------|
| `API_Bearer` | Yes | **Encrypted** Philips SpeechLive API bearer token. Use the ApiKeyEncryptor tool to encrypt your API key before adding it here. |
| `API_Tenant` | Yes | SpeechLive API base URL. For production: `https://sl-api-gateway-apim-prod-cae.azure-api.net` |
| `API_UserAgent` | Yes | User-Agent string sent with API requests. Recommended: `SpeechLive Helper` |
| `Vitalytics_ApiKey` | No | **Encrypted** Vitalytics API key. Only required if `Vitalytics_Enabled=true` in localconfig.ini |
| `Vitalytics_ApiBaseUrl` | No | Vitalytics API base URL. Only required if using Vitalytics monitoring |

#### [Users] Section

The `[Users]` section maps author names (embedded in dictation files) to SpeechLive user accounts.

**Format:** `AuthorID=SpeechLiveUserID`

- **AuthorID**: The numeric author ID configured on the Philips recording device. This is embedded in each dictation file's metadata.
- **SpeechLiveUserID**: The user's identifier in SpeechLive (email or GUID). This can be found in the SpeechLive administration portal.

**Example:**
```ini
[Users]
7777=drsmith@hospital.com
6666=drjones@hospital.com
5555=nursea@hospital.com
```

**Important**: The author ID matching is exact. Ensure the IDs in this section exactly match what is configured on the recording devices.

**Testing**: A sample DS2 file with AuthorID `7777` is provided in the releases for testing your configuration.

## Directory Structure

| Path | Purpose |
|------|---------|
| `C:\ProgramData\SpeechLive Helper\secrets.json` | Encryption key for API tokens |
| `C:\ProgramData\SpeechLive Helper\localconfig.ini` | Local configuration |
| `C:\ProgramData\SpeechLive Helper\Import\` | Drop files here for manual upload |
| `C:\ProgramData\SpeechLive Helper\Working\` | Temporary staging during upload |
| `C:\ProgramData\SpeechLive Helper\Processed\` | Successfully uploaded files |
| `C:\ProgramData\SpeechLive Helper\Error\` | Failed uploads |
| `C:\ProgramData\SpeechLive Helper\Logs\` | Application logs |

## Solution Structure

| Project | Description |
|---------|-------------|
| **SpeechLiveUploader** | Main Windows service application |
| **EncryptionLibrary** | AES-256 encryption for API tokens |
| **ApiKeyEncryptor** | WinForms utility for encrypting tokens |
| **SpeechLiveUploader.Win** | Windows Forms management utility |

## Building from Source

```bash
# Clone the repository
git clone https://github.com/yourusername/speechlive_helper.git

# Build the solution
dotnet build

# Run locally for testing
dotnet run --project SpeechLiveUploader

# Publish as single-file executable
dotnet publish SpeechLiveUploader -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## Setting Up a Local Test Environment

For development and testing, you can simulate a network share using a local shared folder.

### Step 1: Create a Local Share

1. Create a folder for your config files:
   ```cmd
   mkdir C:\SpeechLiveConfig
   ```

2. Open **Command Prompt as Administrator** and create a network share:
   ```cmd
   net share config=C:\SpeechLiveConfig /grant:Everyone,FULL
   ```

3. Verify the share is accessible:
   ```cmd
   dir \\localhost\config
   ```

To remove the share later:
```cmd
net share config /delete
```

### Step 3: Configure Local Config

Create `C:\ProgramData\SpeechLive Helper\localconfig.ini`:
```ini
[Config]
Central_Config_UNC=\\localhost\config\centralconfig.ini
APP_Identifier=DEV-WORKSTATION
Delete_Files_After_Upload=false
Vitalytics_Enabled=false
Log_Retention_Days=30
```

### Step 4: Create Central Config

Place your `centralconfig.ini` in the shared folder (`C:\SpeechLiveConfig\`):
```ini
[Config]
API_Bearer=<your_encrypted_api_token>
API_Tenant=https://sl-api-gateway-apim-prod-cae.azure-api.net
API_UserAgent=SpeechLive Helper

[Users]
TestAuthor=your-speechlive-user-guid
```

### Step 5: Run the Service

```bash
dotnet run --project SpeechLiveUploader
```

### Testing Without a USB Device

You can test the service without a physical dictation device by using the **Import folder**:

1. Ensure the service is running
2. Copy `.ds2` or `.dss` files to `C:\ProgramData\SpeechLive Helper\Import\`
3. The service will automatically detect and process the files
4. Check the logs at `C:\ProgramData\SpeechLive Helper\Logs\` to verify processing

This is also useful for:
- **Re-processing failed files**: Copy files from the `Error` folder back to `Import`
- **Remote testing**: Share the Import folder on the network and copy files from another machine
- **Batch processing**: Copy multiple files at once for bulk uploads

## Installation Locations

### Recommended Deployment Structure

| Component | Location | Notes |
|-----------|----------|-------|
| **Service executable** | `%APPDATA%\SpeechLive Helper\` | Installed by setup |
| **Local configuration** | `C:\ProgramData\SpeechLive Helper\` | Machine-specific settings |
| **Central configuration** | `\\server\share\` | Shared across all workstations |
| **Encryption key** | `C:\ProgramData\SpeechLive Helper\secrets.json` | Must match key used to encrypt tokens |

### For Single Workstation

If deploying to a single machine without network shares:
1. Store `centralconfig.ini` locally (e.g., `C:\ProgramData\SpeechLive Helper\centralconfig.ini`)
2. Set `Central_Config_UNC=C:\ProgramData\SpeechLive Helper\centralconfig.ini` in localconfig.ini

### For Multiple Workstations

For enterprise deployments with multiple upload stations:
1. Store `centralconfig.ini` on a network share accessible to all workstations
2. Each workstation has its own `localconfig.ini` with a unique `APP_Identifier`
3. All workstations share the same `secrets.json` (deploy via installer or group policy)

## Building the Installer

The installer is built using [Inno Setup](https://jrsoftware.org/isinfo.php).

### Prerequisites

1. Install Inno Setup 6.x
2. (Optional) Code signing certificate for signed installers

### Setup

1. Copy `speechlivehelper.iss.template` to `speechlivehelper_prod.iss`
2. Edit `speechlivehelper_prod.iss` and configure:
   - `MyAppPublisher` - Your company name
   - `MyAppURL` - Your website
   - `MyAppPublishDirectory` - Path to published files
   - `MyOutputDir` - Where to save the installer
   - `SignTool` - Your code signing configuration (optional)

### Build Steps

1. Publish the SpeechLiveUploader project:
   ```bash
   dotnet publish SpeechLiveUploader -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
   ```

2. Copy published files to your installer files directory

3. Delete any `.pdb` symbol files from the installer directory

4. Copy support files (`success.wav`, `error.wav`, icons) if not present

5. Open `speechlivehelper_prod.iss` in Inno Setup Compiler

6. Build the installer (Ctrl+F9)

The installer will be created in your output directory as `slhsetup.exe`.

**Note**: The `speechlivehelper_prod.iss` file is excluded from git as it contains environment-specific paths. Use the template to create your own.

## First Run Setup

Before using the service, you must set up the encryption key and encrypt your SpeechLive API token.

### Step 1: Generate Encryption Key

1. Build and run the **ApiKeyEncryptor** project:
   ```bash
   dotnet run --project ApiKeyEncryptor
   ```
2. The tool will show "No encryption key found" on first run
3. Click **"Generate New Encryption Key"**
4. A `secrets.json` file is created in the ApiKeyEncryptor folder

### Step 2: Encrypt Your API Token

1. Enter your Philips® SpeechLive API Bearer token in the text box
2. Click **"Encrypt API Key"**
3. Copy the encrypted result to your `centralconfig.ini` as the `API_Bearer` value

### Step 3: Deploy secrets.json

Copy the `secrets.json` file to each machine where the service runs:
```
C:\ProgramData\SpeechLive Helper\secrets.json
```

The same `secrets.json` must be used on all machines to decrypt the API token.

### Step 4: Configure and Start

1. Set up `localconfig.ini` and `centralconfig.ini` (see Configuration section)
2. Start the "SpeechLive Upload Helper" Windows Service

### Encryption Key Management

| File | Location | Purpose |
|------|----------|---------|
| `secrets.json` | ApiKeyEncryptor folder | Used when encrypting new tokens |
| `secrets.json` | `C:\ProgramData\SpeechLive Helper\` | Used by service to decrypt tokens |

**Important**:
- The same encryption key must be used to encrypt and decrypt tokens
- If you regenerate the key, you must re-encrypt all API tokens and redeploy `secrets.json`
- `secrets.json` is excluded from git - never commit it to source control

### Understanding the Security Model

The `secrets.json` file contains the AES-256 encryption key in plain text. **Anyone with access to both `secrets.json` and the encrypted API token can decrypt it.**

**What this protects against:**
- API keys exposed in source code repositories
- Casual observation of config files (encrypted values look like gibberish)
- Accidental sharing of centralconfig.ini without the key

**What this does NOT protect against:**
- An attacker with file system access to both files
- A compromised workstation where both files are stored

**Real security comes from:**
- Restricting file system permissions on `secrets.json`
- Restricting network share permissions on `centralconfig.ini`
- Never committing `secrets.json` to source control
- Using different encryption keys for different environments (dev/prod)

This is "encryption at rest" - similar to encrypted connection strings in .NET applications. It prevents plain-text credentials from sitting on network shares, but relies on proper access controls for true security.

### Why Not Hardcode the Key in Code?

You might think embedding the encryption key in the compiled code would be more secure, but it's actually **less secure**:

- **.NET assemblies are easily decompiled** - Tools like ILSpy or dnSpy can extract hardcoded strings from any .exe in minutes
- **One key for all deployments** - If it's in the code, every installation uses the same key
- **Can't rotate without rebuilding** - A leaked key requires rebuilding and redeploying the entire application

With `secrets.json`:
- The key is not in the compiled binary
- Each organization can have a unique key
- Keys can be rotated by simply replacing the file
- Attackers need file system access to a specific machine, not just a copy of the installer

### Future Security Enhancements

If your security requirements demand stronger protection, these enhancements could be implemented:

| Option | Description | Trade-off |
|--------|-------------|-----------|
| **DPAPI encryption** | Encrypt secrets.json using Windows Data Protection API | Machine-specific; can't share secrets.json across workstations |
| **Windows Credential Manager** | Store the encryption key in Windows Credential Manager | Requires service account configuration |
| **Hardware Security Module** | Use HSM for key storage | High cost; enterprise-only |
| **Azure Key Vault** | Cloud-based secrets management | Requires Azure subscription and internet connectivity |

If you have specific compliance requirements (HIPAA, SOC2, etc.) and need one of these enhancements, please open an issue to discuss implementation.

## Supported File Formats

- `.ds2` - Philips® Digital Speech Standard Pro format
- `.dss` - Philips®/Olympus® Digital Speech Standard format

### Known Limitations

- **DSS Playback**: Although the Philips® SpeechLive API accepts `.dss` files, we've found that users cannot play them back through the SpeechLive web client. Playback may work in Philips® SpeechExec Pro, but this has not been tested. DSS upload support is retained for compatibility.
- **Encrypted DS2 Files**: Encrypted `.ds2` files are not currently supported. If there is demand for this feature, it can be considered for future development.

## Logging

Logs are stored in `C:\ProgramData\SpeechLive Helper\Logs\` with automatic rotation at 2MB. Previous days' logs are archived to `Logs\Archive\` and purged after the retention period (default 30 days).

## Troubleshooting

### Service Won't Start
- Verify `localconfig.ini` exists and is correctly configured
- Check that `Central_Config_UNC` path is accessible
- Review Windows Event Viewer for startup errors

### Files Not Uploading
- Ensure author name in file matches a `[Users]` entry in central config
- Check API token is correctly encrypted and valid
- Review logs for detailed error messages

### API Key Decryption Errors
If you see "secrets.json not found" or decryption errors in logs:
- Verify `secrets.json` exists at `C:\ProgramData\SpeechLive Helper\secrets.json`
- Ensure the same `secrets.json` was used to encrypt the API token in `centralconfig.ini`
- If the encryption key changed, re-encrypt API tokens using ApiKeyEncryptor and redeploy

### USB Device Not Detected
- Service must be running before connecting the device
- Try disconnecting and reconnecting the device
- Check Windows Device Manager for driver issues

## Vitalytics Integration

The codebase includes integration with [Vitalytics](https://vitalytics.app), our own health monitoring and analytics platform. This feature allows tracking of:
- Service startup/shutdown events
- Upload success/failure metrics
- Heartbeat monitoring
- Error tracking

**Vitalytics is disabled by default.** If you want to use this feature with your own Vitalytics account:
1. Set `Vitalytics_Enabled=true` in `localconfig.ini`
2. Add your encrypted `Vitalytics_ApiKey` and `Vitalytics_ApiBaseUrl` to `centralconfig.ini`

If you don't need remote monitoring, you can safely ignore this feature.

## Trademarks

Philips®, SpeechLive®, SpeechExec®, and Digital Speech Standard® are registered trademarks of Philips or their respective owners. Olympus® is a registered trademark of Olympus Corporation. This project is not affiliated with or endorsed by any of these companies.

## License

Proprietary software. All rights reserved.

## Version

Current version: 1.1.0
