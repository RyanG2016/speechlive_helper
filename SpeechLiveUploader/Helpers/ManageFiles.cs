using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.RegularExpressions;

public class ManageFiles
{
    public static string WorkingFolderPath = @"C:\\ProgramData\SpeechLive Helper\Working\";
    public static string ErrorFolderPath = @"C:\\ProgramData\SpeechLive Helper\Error\";
    public static string LogsFolderPath = @"C:\\ProgramData\SpeechLive Helper\Logs\";
    public static string ConfigurationPath = @"C:\ProgramData\SpeechLive Helper\localconfig.ini";
    public static string ProcessCompletePath = @"C:\ProgramData\SpeechLive Helper\EnglishCopyComplete.wav"; 
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
        else if (File.Exists(path))
        {
            //using (TextWriter tw = new StreamWriter(path))
            //{
            //    tw.WriteLine(info);
            //}
            try
            {
                using (var txtFile = File.AppendText(path))
                {
                    txtFile.WriteLine(info);
                }
            }
            catch { }
        }
    }

    public static Metadata ExtractMetadata(string path)
    {
        Metadata obj = new Metadata();
        const int HEADER_SIZE = 900;
        byte[] bytesFile = new byte[HEADER_SIZE];
        using (FileStream fs = File.OpenRead(path))
        {
            fs.Read(bytesFile, 0, HEADER_SIZE);
            fs.Close();
        }
        string hexValues = BitConverter.ToString(bytesFile);
        string[] hexValuesSplit = hexValues.Split('-');
        string Info = "";
        foreach (string hex in hexValuesSplit)
        {
            // Convert the number expressed in base-16 to an integer.
            int value = Convert.ToInt32(hex, 16);
            // Get the character corresponding to the integral value.
            string stringValue = Char.ConvertFromUtf32(value);
            char charValue = (char)value;
           
            if (hex != "FF")
            {
                //string.Format("hexadecimal value = {0}, int value = {1}, char value = {2} or {3}",
                                        //hex, value, stringValue, charValue);
                if (hex == "00")
                    Info += " ";
                else
                    Info += stringValue;

            }
        }
        Info = ReplaceWhitespace(Info, " ");
        string Aut = "Author";
        string WT = "Work type";

        var regex = new Regex(string.Format(@"(?<!\w){0}\W+(\w+)", Regex.Escape(Aut)));
        var match = regex.Match(Info);
        if (match.Success)
        {
            obj.Author = match.Groups[1].Value;
        }

        regex = new Regex(string.Format(@"(?<!\w){0}\W+(\w+)", Regex.Escape(WT)));

        var match1 = regex.Match(Info);
        if (match1.Success)
        {
            obj.WorkType = match.Groups[1].Value;
        }

        string str = Info.Replace("@", "DevId: ");
        regex = new Regex(string.Format(@"(?<!\w){0}\W+(\w+)", Regex.Escape("DevId: ")));

        var match11 = regex.Match(str);
        if (match11.Success)
        {
            obj.DeviceId  = match11.Groups[1].Value;
        }
        return obj;
    }
}

