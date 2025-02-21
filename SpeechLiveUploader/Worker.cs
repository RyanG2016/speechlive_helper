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

        async void ReadINIFiles(string DSFilesPath)
        {
            List<string> users = new List<string>();
            string configurationPath = ManageFiles.ConfigurationPath; //@"C:\\Speech Live Files\Sample_Local_Configuration.ini";
            //string DSFilesPath = @"F:\\Speech Live Files\";
            string workingFolderPath = ManageFiles.WorkingFolderPath; //System.IO.Directory.GetCurrentDirectory() + "\\Working";
            string AuthorId = "";
            DateTime dateTime;
            try
            {
                IniFile iniFile = new IniFile(configurationPath);
                CreateLogs(DateTime.Now.ToString() + " Configuration File Found And Loaded.");
                LocalConfig.Central_Config_UNC = iniFile.Read("Central_Config_UNC", "Config");
                LocalConfig.APP_Identifier = iniFile.Read("APP_Identifier", "Config");
                LocalConfig.Delete_Files_After_Upload = iniFile.Read("Delete_Files_After_Upload", "Config").ToLower();
                LocalConfig.Drive_Letter_To_Monitor = iniFile.Read("Drive_Letter_To_Monitor", "Config");
                dateTime = DateTime.Now;
                string Info = dateTime.ToString() + " Configuration File Read.";
                CreateLogs(Info);
            }
            catch
            {
                CreateLogs(DateTime.Now.ToString() + " Configuration File Not Found.");
            }

            try
            {
                IniFile iniFile = new IniFile(LocalConfig.Central_Config_UNC);
                dateTime = DateTime.Now;
                string Info1 = dateTime.ToString() + " Central Configuration File Found And Loaded.";
                CreateLogs(Info1);
                CentralConfig.API_Bearer = iniFile.Read("API_Bearer", "Config");
                CentralConfig.API_UserAgent = iniFile.Read("API_UserAgent", "Config");
                CentralConfig.API_Tenant = iniFile.Read("API_Tenant", "Config");
                CentralConfig.User1 = iniFile.Read("7777", "Users");
                CentralConfig.User2 = iniFile.Read("7765", "Users");
                users = new IniFile(LocalConfig.Central_Config_UNC).GetUsers();
                CentralConfig.API_Bearer = new IniFile(LocalConfig.Central_Config_UNC).GetToken().Split('=', (StringSplitOptions)0)[1].ToString();
                dateTime = DateTime.Now;
                string Info2 = dateTime.ToString() + " Central Configuration Read.";
                CreateLogs(Info2);
            }
            catch
            {
                CreateLogs(DateTime.Now.ToString() + " Central Configuration File Not Found.");
            }


            var GetDs2Files = new ManageFiles().GetDs2Files(DSFilesPath);
            var GetDssFiles = new ManageFiles().GetDsFiles(DSFilesPath);
            string[] strArray;
            int index;

            //check for ds2 files
            if (GetDs2Files.Length > 0)
            {
                CreateLogs(DateTime.Now.ToString() + " Connected device contain supported file formats (ds2)");
                foreach (var ds2 in GetDs2Files)
                {
                    try
                    {
                        string fileName = DateTime.Now.Date.ToString("MMddyyyy") + DateTime.Now.Ticks +  Path.GetFileName(ds2);
                        var objm = ManageFiles.ExtractMetadata(ds2);
                        CreateLogs(DateTime.Now.ToString() + " " + objm.Author);
                        CreateLogs(DateTime.Now.ToString() + " " + objm.WorkType);

                        if (users != null)
                        {
                            foreach (string str in users)
                            {
                                string[] parts = str.Split('=', StringSplitOptions.None);
                                if (parts[0] == objm.Author)
                                {
                                    AuthorId = parts[1];
                                    break;
                                }
                            }
                        }

                        if (AuthorId != "")
                        {
                            try
                            {
                                dateTime = DateTime.Now;
                                dateTime = dateTime.Date;
                                string str1 = dateTime.ToString("MMddyyyy");
                                dateTime = DateTime.Now;
                                string str2 = dateTime.Ticks.ToString();
                                string fileName1 = Path.GetFileName(ds2);
                                fileName = str1 + str2 + fileName1;
                                dateTime = DateTime.Now;
                                string Info6 = dateTime.ToString() + " Calling Api";
                                CreateLogs(Info6);
                                string response = "";
                                await Task.Run((Func<Task>)(async () =>
                                {
                                    File.Copy(ds2, workingFolderPath + @"\" + fileName, true);
                                    CreateLogs(DateTime.Now.ToString() + ds2 + " copied from device to application");
                                    CreateLogs(DateTime.Now.ToString() + " Calling Api As " + AuthorId);

                                    response = await new ApiHelper().PostHistoryAsync(AuthorId, "0", objm.WorkType, objm.DeviceId, workingFolderPath + "\\" + fileName);
                                    CreateLogs(DateTime.Now.ToString() + " Api response : " + response);
                                    DateTime now;
                                    if (response.StartsWith("Success"))
                                    {
                                        now = DateTime.Now;
                                        CreateLogs(now.ToString() + ds2 + " copied from device to application");
                                    }
                                    else
                                    {
                                        now = DateTime.Now;
                                        this.CreateLogs(now.ToString() + ds2 + " found error for this file while calling api , check error folder.");
                                        File.Copy(ds2, ManageFiles.ErrorFolderPath + "\\" + fileName, true);
                                        File.Delete(workingFolderPath + "\\" + fileName);
                                        now = DateTime.Now;
                                        this.CreateLogs(now.ToString() + ds2 + " copied from device to error folder.");
                                    }
                                    if (LocalConfig.Delete_Files_After_Upload != "true")
                                        return;
                                    
                                    now = DateTime.Now;
                                    this.CreateLogs(now.ToString() + ds2 + " file delete from external device.");
                                    File.Delete(ds2);
                                }));
                            }
                            catch
                            {
                                CreateLogs(DateTime.Now.ToString() + ds2 + " doesn’t copied from device to application");
                            }
                        }
                        else
                        {
                            dateTime = DateTime.Now;
                            string Info7 = dateTime.ToString() + ds2 + " invalid author record found, check error folder.";
                            CreateLogs(Info7);
                            dateTime = DateTime.Now;
                            dateTime = dateTime.Date;
                            string str3 = dateTime.ToString("MMddyyyy");
                            dateTime = DateTime.Now;
                            string str4 = dateTime.Ticks.ToString();
                            fileName = Path.GetFileName(ds2);
                            string str5 = str3 + str4 + fileName;
                            File.Copy(ds2, ManageFiles.ErrorFolderPath + "\\" + str5, true);
                            dateTime = DateTime.Now;
                            string Info8 = dateTime.ToString() + ds2 + " copied from device to error folder.";
                            CreateLogs(Info8);
                            if (LocalConfig.Delete_Files_After_Upload == "true")
                            {
                                dateTime = DateTime.Now;
                                string Info9 = dateTime.ToString() + ds2 + " file delete from external device.";
                                CreateLogs(Info9);
                                File.Delete(ds2);
                            }
                            strArray = null;
                        }      

                        if (LocalConfig.Delete_Files_After_Upload == "true") {
                            CreateLogs(DateTime.Now.ToString() + ds2 + " file delete from external device.");
                            File.Delete(ds2); 
                        }
                    }
                    catch
                    {
                        CreateLogs(DateTime.Now.ToString() + ds2 + " doesn’t copied from device to application");
                    }       

                }

            }
            else
            {
                CreateLogs(DateTime.Now.ToString() + " Connected device doesn’t contain supported file formats (ds2)");
            }
           

            //check for dss files
            if (GetDssFiles.Length > 0)
            {
                CreateLogs(DateTime.Now.ToString() + " Connected device contain supported file formats (dss)");
                foreach (var dss in GetDssFiles)
                {
                    try
                    {
                        AuthorId = "";
                        string fileName = DateTime.Now.Date.ToString("MMddyyyy") + DateTime.Now.Ticks + Path.GetFileName(dss);
                        var objm = ManageFiles.ExtractMetadata(dss);
                        CreateLogs(DateTime.Now.ToString() + " " + objm.Author);
                        CreateLogs(DateTime.Now.ToString() + " " + objm.WorkType);

                        if (users != null)
                        {
                            foreach (string str in users)
                            {
                                string[] parts = str.Split('=', StringSplitOptions.None);
                                if (parts[0] == objm.Author)
                                {
                                    AuthorId = parts[1];
                                    break;
                                }

                            }
                        }

                        if (AuthorId != "")
                        {
                            try
                            {
                                dateTime = DateTime.Now;
                                dateTime = dateTime.Date;
                                string str1 = dateTime.ToString("MMddyyyy");
                                dateTime = DateTime.Now;
                                string str2 = dateTime.Ticks.ToString();
                                string fileName2 = Path.GetFileName(dss);
                                fileName = str1 + str2 + fileName2;
                                dateTime = DateTime.Now;
                                string Info13 = dateTime.ToString() + " Calling Api";
                                CreateLogs(Info13);
                                string response = "";

                                await Task.Run((Func<Task>)(async () =>
                                {
                                    File.Copy(dss, workingFolderPath + @"\" + fileName, true);
                                    CreateLogs(DateTime.Now.ToString() + dss + " copied from device to application");
                                    CreateLogs(DateTime.Now.ToString() + " Calling Api As " + AuthorId);

                                    response = await new ApiHelper().PostHistoryAsync(objm.AuthorID, "0", objm.WorkType, objm.DeviceId, workingFolderPath + @"\" + fileName);
                                    CreateLogs(DateTime.Now.ToString() + " Api response : " + response);
                                    DateTime now;
                                    if (response.StartsWith("Success"))
                                    {
                                        now = DateTime.Now;
                                        CreateLogs(now.ToString() + dss+ " copied from device to application");
                                    }
                                    else
                                    {
                                        now = DateTime.Now;
                                        this.CreateLogs(now.ToString() + dss + " found error for this file while calling api , check error folder.");
                                        File.Copy(dss, ManageFiles.ErrorFolderPath + "\\" + fileName, true);
                                        File.Delete(workingFolderPath + "\\" + fileName);
                                        now = DateTime.Now;
                                        this.CreateLogs(now.ToString() + dss + " copied from device to error folder.");
                                    }
                                    if (LocalConfig.Delete_Files_After_Upload != "true")
                                        return;

                                    now = DateTime.Now;
                                    this.CreateLogs(now.ToString() + dss + " file delete from external device.");
                                    File.Delete(dss);
                                }));
                            }
                            catch
                            {
                                CreateLogs(DateTime.Now.ToString() + dss + " doesn’t copied from device to application");
                            }

                        }
                        else
                        {
                                dateTime = DateTime.Now;
                                string Info14 = dateTime.ToString() + dss + " invalid author record found, check error folder.";
                                CreateLogs(Info14);
                                dateTime = DateTime.Now;
                                dateTime = dateTime.Date;
                                string str8 = dateTime.ToString("MMddyyyy");
                                dateTime = DateTime.Now;
                                string str9 = dateTime.Ticks.ToString();
                                fileName = Path.GetFileName(dss);
                                string str10 = str8 + str9 + fileName;
                                File.Copy(dss, ManageFiles.ErrorFolderPath + "\\" + str10, true);
                                dateTime = DateTime.Now;
                                string Info15 = dateTime.ToString() + dss + " copied from device to error folder.";
                                CreateLogs(Info15);

                            if (LocalConfig.Delete_Files_After_Upload == "true")
                            {
                                CreateLogs(DateTime.Now.ToString() + dss + " file delete from external device.");
                                File.Delete(dss);
                            }
                            strArray = null;

                        }

                      
                    }
                    catch
                    {
                        CreateLogs(DateTime.Now.ToString() + dss + " doesn’t copied from device to application");
                    }

                }

            }
            else
                CreateLogs(DateTime.Now.ToString() + " Connected device doesn’t contain supported file formats (dss)");


            //play process complete sound.
            Process.Start(@"powershell", $@"-c (New-Object Media.SoundPlayer '{ManageFiles.ProcessCompletePath}').PlaySync();");
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
