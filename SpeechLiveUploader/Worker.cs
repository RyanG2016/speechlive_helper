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
    public class Worker : BackgroundService
    {
        static bool isProcessing = false;
        private readonly ILogger<Worker> _logger;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;               

            if (!Directory.Exists(ManageFiles.WorkingFolderPath))
                Directory.CreateDirectory(ManageFiles.WorkingFolderPath);
            if (!Directory.Exists(ManageFiles.LogsFolderPath))
                Directory.CreateDirectory(ManageFiles.LogsFolderPath);
            if (!Directory.Exists(ManageFiles.ErrorFolderPath))
                Directory.CreateDirectory(ManageFiles.ErrorFolderPath);

            CreateLogs(DateTime.Now.ToString() + " Application Started.");
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //while (!stoppingToken.IsCancellationRequested)
            //{
                //if (_logger.IsEnabled(LogLevel.Information))
                //{
                //    _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                //}


                CheckForEvents();

                await Task.Delay(1000, stoppingToken);
            //}
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
        /// Reads the configuration files and processes the files in the specified folder.
        /// </summary>
        /// <param name="DSFilesPath">The path to the folder containing the files to process.</param>
        /// <remarks>
        /// This function reads the configuration files and processes the files in the specified folder.
        /// It handles both .ds2 and .dss files.
        /// If the file is valid, it calls the API with the appropriate parameters and handles the response.
        /// If the file is invalid, it moves the file to the error folder.
        /// </remarks>
        async void ReadINIFiles(string DSFilesPath)
        {
            List<string> users = new List<string>();
            string configurationPath = ManageFiles.ConfigurationPath;
            string workingFolderPath = ManageFiles.WorkingFolderPath;
            string AuthorId = "";
            bool hasError = false;


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
            catch
            {
                hasError = true;
                CreateLogs(DateTime.Now.ToString() + " Configuration File Not Found.");
            }

            try
            {
                IniFile iniFile = new IniFile(LocalConfig.Central_Config_UNC);
                CreateLogs(DateTime.Now.ToString() + " Central Configuration File Found And Loaded.");
                CentralConfig.API_Bearer = iniFile.Read("API_Bearer", "Config");
                CentralConfig.API_UserAgent = iniFile.Read("API_UserAgent", "Config");
                CentralConfig.API_Tenant = iniFile.Read("API_Tenant", "Config");
               // CentralConfig.User1 = iniFile.Read("7777", "Users"); //Why is this hardcoded?
                //CentralConfig.User2 = iniFile.Read("7765", "Users"); //Why is this hardcoded?
                users = iniFile.GetUsers();
                //CentralConfig.API_Bearer = iniFile.GetToken().Split('=')[1];
                CreateLogs(DateTime.Now.ToString() + " Central Configuration Read.");
            }
            catch
            {
                hasError = true;
                CreateLogs(DateTime.Now.ToString() + " Central Configuration File Not Found.");
            }

            var GetDs2Files = new ManageFiles().GetDs2Files(DSFilesPath);
            var GetDssFiles = new ManageFiles().GetDsFiles(DSFilesPath);

            // === Handle DS2 Files ===
            if (GetDs2Files.Length > 0)
            {
                CreateLogs(DateTime.Now.ToString() + " Connected device contains one or more files with supported file formats (ds2)");
                foreach (var ds2 in GetDs2Files)
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
                                string response = "";

                                await Task.Run(async () =>
                                {
                                    File.Copy(ds2, Path.Combine(workingFolderPath, fileName), true);
                                    CreateLogs(DateTime.Now.ToString() + ds2 + " copied from device to application");
                                    CreateLogs(DateTime.Now.ToString() + " Calling Api As " + AuthorId);

                                    //response = await new ApiHelper().PostHistoryAsync(AuthorId, "0", objm.WorkType, objm.DeviceId, Path.Combine(workingFolderPath, fileName));
                                    HttpResponseMessage response = await new ApiHelper().PostHistoryAsync(AuthorId, "0", objm.WorkType, objm.DeviceId, Path.Combine(workingFolderPath, fileName));
                                    string content = await response.Content.ReadAsStringAsync();
                                    CreateLogs(DateTime.Now.ToString() + " Api response : " + content);

                                    if (response.IsSuccessStatusCode)
                                    {
                                        CreateLogs(DateTime.Now.ToString() + ds2 + " successfully processed.");
                                    }
                                    else
                                    {
                                        hasError = true;
                                        CreateLogs(DateTime.Now.ToString() + ds2 + " found error for this file while calling api, check error folder.");
                                        File.Copy(ds2, Path.Combine(ManageFiles.ErrorFolderPath, fileName), true);
                                        File.Delete(Path.Combine(workingFolderPath, fileName));
                                        CreateLogs(DateTime.Now.ToString() + ds2 + " copied from device to error folder.");
                                    }

                                    if (LocalConfig.Delete_Files_After_Upload == "true")
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
                            hasError = true;
                            string errorFileName = GenerateTimestampedFileName(ds2);
                            CreateLogs(DateTime.Now.ToString() + ds2 + " invalid author record found, check error folder.");
                            File.Copy(ds2, Path.Combine(ManageFiles.ErrorFolderPath, errorFileName), true);
                            CreateLogs(DateTime.Now.ToString() + ds2 + " copied from device to error folder.");

                            if (LocalConfig.Delete_Files_After_Upload == "true")
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
            if (GetDssFiles.Length > 0)
            {
                CreateLogs(DateTime.Now.ToString() + " Connected device contains supported file formats (dss)");
                foreach (var dss in GetDssFiles)
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
                                string response = "";

                                await Task.Run(async () =>
                                {
                                    File.Copy(dss, Path.Combine(workingFolderPath, fileName), true);
                                    CreateLogs(DateTime.Now.ToString() + dss + " copied from device to application");
                                    CreateLogs(DateTime.Now.ToString() + " Calling Api As " + AuthorId);

                                    HttpResponseMessage response = await new ApiHelper().PostHistoryAsync(AuthorId, "0", objm.WorkType, objm.DeviceId, Path.Combine(workingFolderPath, fileName));
                                    string content = await response.Content.ReadAsStringAsync();
                                    CreateLogs(DateTime.Now.ToString() + " Api response : " + content);
                                    if (response.IsSuccessStatusCode)
                                    {
                                        CreateLogs(DateTime.Now.ToString() + dss + " successfully processed.");
                                    }
                                    else
                                    {
                                        hasError = true;
                                        CreateLogs(DateTime.Now.ToString() + dss + " found error while calling api, check error folder.");
                                        File.Copy(dss, Path.Combine(ManageFiles.ErrorFolderPath, fileName), true);
                                        File.Delete(Path.Combine(workingFolderPath, fileName));
                                        CreateLogs(DateTime.Now.ToString() + dss + " copied from device to error folder.");
                                    }

                                    if (LocalConfig.Delete_Files_After_Upload == "true")
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
                            hasError = true;
                            string errorFileName = GenerateTimestampedFileName(dss);
                            CreateLogs(DateTime.Now.ToString() + dss + " invalid author record found, check error folder.");
                            File.Copy(dss, Path.Combine(ManageFiles.ErrorFolderPath, errorFileName), true);
                            CreateLogs(DateTime.Now.ToString() + dss + " copied from device to error folder.");

                            if (LocalConfig.Delete_Files_After_Upload == "true")
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

            // === Final Audio Cue ===
            string soundPath = hasError ? ManageFiles.ErrorSoundPath : ManageFiles.ProcessCompletePath;
            Process.Start("powershell", $@"-c (New-Object Media.SoundPlayer '{soundPath}').PlaySync();");

            users = null;
            GetDs2Files = null;
            GetDssFiles = null;
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

                ReadINIFiles(path);
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
                ReadINIFiles(path);
            }
        }

        void CreateLogs(string Info)
        {
            //var path = System.IO.Directory.GetCurrentDirectory() + "\\Logs";
            var path = ManageFiles.LogsFolderPath;
            var LogFiles = new ManageFiles().GetLogFiles(path);
            if (LogFiles.Length > 0)
            {
                ManageFiles.CreateAndAppendLogs(LogFiles[0], Info);
            }
            else
            {
                string fileName = DateTime.Now.Date.ToString("MMddyyyy") + "_Logfile.txt";
                ManageFiles.CreateAndAppendLogs(path + @"\" + fileName, Info);
            }


        }
    }
}
