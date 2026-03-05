using System;
using System.IO;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;

namespace MetadataTestApp
{
    public class ManageFiles
    {
        public static string WorkingFolderPath = @"C:\\ProgramData\SpeechLive Helper\Working\";
        public static string ErrorFolderPath = @"C:\\ProgramData\SpeechLive Helper\Error\";
        public static string LogsFolderPath = @"C:\\ProgramData\SpeechLive Helper\Logs\";
        public static string ConfigurationPath = @"C:\ProgramData\SpeechLive Helper\localconfig.ini";
        public static string ProcessCompletePath = @"C:\ProgramData\SpeechLive Helper\success.wav";
        public static string ErrorSoundPath = @"C:\ProgramData\SpeechLive Helper\error.wav"; // Added for failed sound



        private static readonly Regex sWhitespace = new Regex(@"\s+");



        public static string ReplaceWhitespace(string input, string replacement)
        {
            return sWhitespace.Replace(input, replacement);
        }



        public string[] GetDs2Files(string path)
        {
            return Directory.GetFiles(path, "*.ds2");
        }



        public string[] GetDsFiles(string path)
        {
            return Directory.GetFiles(path, "*.dss");
        }



        public string[] GetLogFiles(string path)
        {
            return Directory.GetFiles(path, "*.txt");
        }



        public static void CreateAndAppendLogs(string path, string info)
        {
            if (!File.Exists(path))
            {
                File.Create(path).Dispose();
                using (TextWriter tw = new StreamWriter(path))
                {
                    tw.WriteLine(info);
                }
            }
            else
            {
                try
                {
                    using (var txtFile = File.AppendText(path))
                    {
                        txtFile.WriteLine(info);
                    }
                }
                catch
                {
                    // Handle or log error if needed
                }
            }
        }



        private static string ExtractDeviceId(string input)
        {
            string deviceId = "Unknown";



            // First try: match @ followed by 12 digits
            string str = ExtractField(input, "@");
            deviceId = ExtractDeviceIdFromString(str) ?? deviceId;



            // If @ did not return a valid device ID, try % followed by 12 digits
            if (deviceId == "Unknown")
            {
                str = ExtractField(input, "%");
                deviceId = ExtractDeviceIdFromString(str) ?? deviceId;
            }



            return deviceId;
        }



        private static string ExtractDeviceIdFromString(string str)
        {
            if (!string.IsNullOrEmpty(str) && str.Length >= 12)
            {
                str = str.Substring(0, 12);
                var match = Regex.Match(str, @"^\d{12}$");



                if (match.Success)
                {
                    return match.Groups[0].Value;
                }
            }
            return "Unknown";
        }



        private static string ExtractField(string input, string label)
        {
            var regex = new Regex($@"(?<!\w){Regex.Escape(label)}\W*(\S+)");
            var match = regex.Match(input);
            return match.Success ? match.Groups[1].Value : null;
        }



        public static Metadata ExtractMetadata(string path)
        {
            Metadata obj = new Metadata();
            const int HEADER_SIZE = 900;
            byte[] bytesFile = new byte[HEADER_SIZE];



            using (FileStream fs = File.OpenRead(path))
            {
                fs.Read(bytesFile, 0, HEADER_SIZE);
            }



            string hexValues = BitConverter.ToString(bytesFile);
            string[] hexValuesSplit = hexValues.Split('-');
            string Info = "";



            foreach (string hex in hexValuesSplit)
            {
                if (hex != "FF")
                {
                    int value = Convert.ToInt32(hex, 16);
                    string stringValue = Char.ConvertFromUtf32(value);



                    if (hex == "00")
                        Info += " ";
                    else
                        Info += stringValue;
                }
            }



            Info = ReplaceWhitespace(Info, " ");



            Console.WriteLine(Info);



            obj.Author = ExtractField(Info, "Author");



            obj.WorkType = ExtractField(Info, "Work Type");



            if (obj.WorkType is null)
            {
                obj.WorkType = ExtractField(Info, "Work type");
            }



            obj.DeviceId = ExtractDeviceId(Info);



            return obj;
        }

    }



    public class Metadata
    {
        public string Author { get; set; }
        public string WorkType { get; set; }
        public string DeviceId { get; set; }
    }



    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Metadata Extractor ===");
            Console.WriteLine("Enter 'exit' to quit.\n");



            while (true)
            {
                Console.Write("Enter full path to the audio file: ");
                string path = Console.ReadLine();



                if (string.Equals(path, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Exiting...");
                    break;
                }



                if (!File.Exists(path))
                {
                    Console.WriteLine("File not found.\n");
                    continue;
                }



                try
                {
                    Metadata metadata = ManageFiles.ExtractMetadata(path);



                    Console.WriteLine("\nMetadata Extracted:");
                    Console.WriteLine($"Author : {metadata.Author}");
                    Console.WriteLine($"WorkType : {metadata.WorkType}");
                    Console.WriteLine($"DeviceId : {metadata.DeviceId}\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error extracting metadata: {ex.Message}\n");
                }
            }
        }
    }

}
