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
        private readonly ILogger<Worker> _logger;
        private FileSystemWatcher? _importFolderWatcher;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;               

            if (!Directory.Exists(ManageFiles.WorkingFolderPath))
                Directory.CreateDirectory(ManageFiles.WorkingFolderPath);
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


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //while (!stoppingToken.IsCancellationRequested)
            //{
                //if (_logger.IsEnabled(LogLevel.Information))
                //{
                //    _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                //}

                // Check Import folder for any existing files on startup
                CheckImportFolderOnStartup();

                CheckForEvents();

                await Task.Delay(1000, stoppingToken);
            //}
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
            var ds2Files = new ManageFiles().GetDs2Files(DSFilesPath);
            var dssFiles = new ManageFiles().GetDsFiles(DSFilesPath);
            int totalFiles = ds2Files.Length + dssFiles.Length;

            if (totalFiles > 0)
            {
                string sourceType = source == FileSource.Import ? "import folder" : "USB device";
                CreateLogs($"{DateTime.Now} === Processing started from {sourceType}: {totalFiles} file(s) found ({ds2Files.Length} .ds2, {dssFiles.Length} .dss) ===");
            }

            try
            {
                IniFile iniFile = new IniFile(configurationPath);
                CreateLogs(DateTime.Now.ToString() + " Configuration File Found And Loaded.");
                LocalConfig.Central_Config_UNC = iniFile.Read("Central_Config_UNC", "Config");
                LocalConfig.APP_Identifier = iniFile.Read("APP_Identifier", "Config");
                LocalConfig.Delete_Files_After_Upload = iniFile.Read("Delete_Files_After_Upload", "Config").ToLower();
                LocalConfig.Drive_Letter_To_Monitor = iniFile.Read("Drive_Letter_To_Monitor", "Config");
                CreateLogs(DateTime.Now.ToString() + " Configuration File Read.");
            }
            catch (Exception ex)
            {
                hasError = true;
                CreateLogs(DateTime.Now.ToString() + " Error reading local configuration file: " + ex.Message);
                return; // Stop processing if local config can't be read
            }

            try
            {
                if (!File.Exists(LocalConfig.Central_Config_UNC))
                {
                    hasError = true;
                    CreateLogs(DateTime.Now.ToString() + " Central Configuration File not found at: " + LocalConfig.Central_Config_UNC);
                    return; // Stop processing if central config doesn't exist
                }

                IniFile iniFile = new IniFile(LocalConfig.Central_Config_UNC);
                CreateLogs(DateTime.Now.ToString() + " Central Configuration File Found And Loaded.");
                
                string encryptedApiKey = iniFile.Read("API_Bearer", "Config");
                if (string.IsNullOrEmpty(encryptedApiKey))
                {
                    hasError = true;
                    CreateLogs(DateTime.Now.ToString() + " API_Bearer not found in central configuration.");
                    return; // Stop processing if API key is missing
                }

                CentralConfig.API_Bearer = encryptedApiKey;
                CentralConfig.API_UserAgent = iniFile.Read("API_UserAgent", "Config");
                CentralConfig.API_Tenant = iniFile.Read("API_Tenant", "Config");
                
                if (string.IsNullOrEmpty(CentralConfig.API_Tenant))
                {
                    hasError = true;
                    CreateLogs(DateTime.Now.ToString() + " API_Tenant not found in central configuration.");
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
                return; // Stop processing if central config can't be read
            }

            // If we got here with an error, stop processing
            if (hasError)
            {
                CreateLogs(DateTime.Now.ToString() + " Stopping file processing due to configuration errors.");
                return;
            }

            // === Handle DS2 Files ===
            if (ds2Files.Length > 0)
            {
                CreateLogs(DateTime.Now.ToString() + " Connected device contains one or more files with supported file formats (ds2)");
                foreach (var ds2 in ds2Files)
                {
                    try
                    {
                        string fileName = GenerateTimestampedFileName(ds2);
                        var objm = ManageFiles.ExtractMetadata(ds2);
                        CreateLogs($"Author: {objm.Author}");
                        CreateLogs($"Worktype: {objm.WorkType}");
                        CreateLogs($"Device ID: {objm.DeviceId}");

                        AuthorId = users.FirstOrDefault(u => u.StartsWith(objm.Author + "="))?.Split('=')[1] ?? "";

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

                                    //response = await new ApiHelper().PostHistoryAsync(AuthorId, "0", objm.WorkType, objm.DeviceId, Path.Combine(workingFolderPath, fileName));
                                    HttpResponseMessage response = await new ApiHelper().PostHistoryAsync(AuthorId, "0", objm.WorkType, objm.DeviceId, Path.Combine(workingFolderPath, workingFileName));
                                    string content = await response.Content.ReadAsStringAsync();

                                    // Log full API response details
                                    if (response.IsSuccessStatusCode)
                                    {
                                        successCount++;
                                        CreateLogs($"{DateTime.Now} Api Success: {content}");
                                        CreateLogs($"{DateTime.Now} {ds2} successfully processed.");

                                        // For Import files, delete from Import folder after successful upload
                                        if (source == FileSource.Import)
                                        {
                                            File.Delete(ds2);
                                            CreateLogs($"{DateTime.Now} {ds2} deleted from import folder.");
                                        }
                                    }
                                    else
                                    {
                                        errorCount++;
                                        hasError = true;
                                        CreateLogs($"{DateTime.Now} Api Error - StatusCode: {(int)response.StatusCode} ({response.ReasonPhrase}), Content: {content}");
                                        CreateLogs($"{DateTime.Now} {ds2} found error while calling api, check error folder.");

                                        File.Copy(ds2, Path.Combine(ManageFiles.ErrorFolderPath, fileName), true);
                                        File.Delete(Path.Combine(workingFolderPath, workingFileName));
                                        CreateLogs($"{DateTime.Now} {ds2} copied from {sourceType} to error folder.");

                                        // For Import files, always delete from Import after copying to Error
                                        if (source == FileSource.Import)
                                        {
                                            File.Delete(ds2);
                                            CreateLogs($"{DateTime.Now} {ds2} deleted from import folder.");
                                        }
                                    }

                                    // For USB files, handle deletion based on config
                                    if (source == FileSource.USB && LocalConfig.Delete_Files_After_Upload == "true")
                                    {
                                        File.Delete(ds2);
                                        CreateLogs(DateTime.Now.ToString() + ds2 + " file deleted from external device.");
                                    }
                                });
                            }
                            catch
                            {
                                hasError = true;
                                CreateLogs(DateTime.Now.ToString() + ds2 + " doesn\'t copied from device to application");
                            }
                        }
                        else
                        {
                            errorCount++;
                            hasError = true;
                            string errorFileName = GenerateTimestampedFileName(ds2);
                            string sourceType = source == FileSource.Import ? "import folder" : "device";
                            CreateLogs(DateTime.Now.ToString() + ds2 + " invalid author record found, check error folder.");
                            File.Copy(ds2, Path.Combine(ManageFiles.ErrorFolderPath, errorFileName), true);
                            CreateLogs(DateTime.Now.ToString() + ds2 + $" copied from {sourceType} to error folder.");

                            // For Import files, always delete after copying to Error
                            if (source == FileSource.Import)
                            {
                                File.Delete(ds2);
                                CreateLogs($"{DateTime.Now} {ds2} deleted from import folder.");
                            }
                            // For USB files, handle deletion based on config
                            else if (LocalConfig.Delete_Files_After_Upload == "true")
                            {
                                File.Delete(ds2);
                                CreateLogs(DateTime.Now.ToString() + ds2 + " file deleted from external device.");
                            }
                        }
                    }
                    catch
                    {
                        hasError = true;
                        CreateLogs(DateTime.Now.ToString() + ds2 + " doesn\'t copied from device to application");
                    }
                }
            }
            else
            {
                //hasError = true;
                CreateLogs(DateTime.Now.ToString() + " Connected device doesn\'t contain supported file formats (ds2)");
            }

            // === Handle DSS Files ===
            if (dssFiles.Length > 0)
            {
                CreateLogs(DateTime.Now.ToString() + " Connected device contains supported file formats (dss)");
                foreach (var dss in dssFiles)
                {
                    try
                    {
                        AuthorId = "";
                        string fileName = GenerateTimestampedFileName(dss);
                        var objm = ManageFiles.ExtractMetadata(dss);
                        CreateLogs($"Author: {objm.Author}");
                        CreateLogs($"Worktype: {objm.WorkType}");
                        CreateLogs($"Device ID: {objm.DeviceId}");

                        AuthorId = users.FirstOrDefault(u => u.StartsWith(objm.Author + "="))?.Split('=')[1] ?? "";

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

                                    HttpResponseMessage response = await new ApiHelper().PostHistoryAsync(AuthorId, "0", objm.WorkType, objm.DeviceId, Path.Combine(workingFolderPath, workingFileName));
                                    string content = await response.Content.ReadAsStringAsync();

                                    // Log full API response details
                                    if (response.IsSuccessStatusCode)
                                    {
                                        successCount++;
                                        CreateLogs($"{DateTime.Now} Api Success: {content}");
                                        CreateLogs($"{DateTime.Now} {dss} successfully processed.");

                                        // For Import files, delete from Import folder after successful upload
                                        if (source == FileSource.Import)
                                        {
                                            File.Delete(dss);
                                            CreateLogs($"{DateTime.Now} {dss} deleted from import folder.");
                                        }
                                    }
                                    else
                                    {
                                        errorCount++;
                                        hasError = true;
                                        CreateLogs($"{DateTime.Now} Api Error - StatusCode: {(int)response.StatusCode} ({response.ReasonPhrase}), Content: {content}");
                                        CreateLogs($"{DateTime.Now} {dss} found error while calling api, check error folder.");

                                        File.Copy(dss, Path.Combine(ManageFiles.ErrorFolderPath, fileName), true);
                                        File.Delete(Path.Combine(workingFolderPath, workingFileName));
                                        CreateLogs($"{DateTime.Now} {dss} copied from {sourceType} to error folder.");

                                        // For Import files, always delete from Import after copying to Error
                                        if (source == FileSource.Import)
                                        {
                                            File.Delete(dss);
                                            CreateLogs($"{DateTime.Now} {dss} deleted from import folder.");
                                        }
                                    }

                                    // For USB files, handle deletion based on config
                                    if (source == FileSource.USB && LocalConfig.Delete_Files_After_Upload == "true")
                                    {
                                        File.Delete(dss);
                                        CreateLogs(DateTime.Now.ToString() + dss + " file deleted from external device.");
                                    }
                                });
                            }
                            catch
                            {
                                hasError = true;
                                CreateLogs(DateTime.Now.ToString() + dss + " wasn\'t copied from device to application");
                            }
                        }
                        else
                        {
                            errorCount++;
                            hasError = true;
                            string errorFileName = GenerateTimestampedFileName(dss);
                            string sourceType = source == FileSource.Import ? "import folder" : "device";
                            CreateLogs(DateTime.Now.ToString() + dss + " invalid author record found, check error folder.");
                            File.Copy(dss, Path.Combine(ManageFiles.ErrorFolderPath, errorFileName), true);
                            CreateLogs(DateTime.Now.ToString() + dss + $" copied from {sourceType} to error folder.");

                            // For Import files, always delete after copying to Error
                            if (source == FileSource.Import)
                            {
                                File.Delete(dss);
                                CreateLogs($"{DateTime.Now} {dss} deleted from import folder.");
                            }
                            // For USB files, handle deletion based on config
                            else if (LocalConfig.Delete_Files_After_Upload == "true")
                            {
                                File.Delete(dss);
                                CreateLogs(DateTime.Now.ToString() + dss + " file deleted from external device.");
                            }
                        }
                    }
                    catch
                    {
                        hasError = true;
                        CreateLogs(DateTime.Now.ToString() + dss + "wasn\'t copied from device to application");
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

            users = null;
            ds2Files = null;
            dssFiles = null;
        }

        async void CheckForEvents()
        {

            IUsbEventWatcher usbEventWatcher = new UsbEventWatcher();

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

            usbEventWatcher.UsbDriveMounted += (_, path) =>
            {
                CreateLogs(DateTime.Now.ToString() + " External device detected (Drive " + path + ")");

                ReadINIFiles(path, FileSource.USB);
            };
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
            //var path = System.IO.Directory.GetCurrentDirectory() + "\\Logs";
            var path = ManageFiles.LogsFolderPath;
            var LogFiles = new ManageFiles().GetLogFiles(path);
            string logFilePath;

            if (LogFiles.Length > 0)
            {
                logFilePath = LogFiles[0];
            }
            else
            {
                string fileName = DateTime.Now.Date.ToString("MMddyyyy") + "_Logfile.txt";
                logFilePath = Path.Combine(path, fileName);
            }

            // Check if log file needs rotation (1MB = 1048576 bytes)
            const long MAX_LOG_SIZE = 1048576;
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
    }
}
