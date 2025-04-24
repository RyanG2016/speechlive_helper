using System.Text.RegularExpressions;

public class ManageFiles
{
    public static string WorkingFolderPath = @"C:\\ProgramData\SpeechLive Helper\Working\";
    public static string ErrorFolderPath = @"C:\\ProgramData\SpeechLive Helper\Error\";
    public static string LogsFolderPath = @"C:\\ProgramData\SpeechLive Helper\Logs\";
    public static string ConfigurationPath = @"C:\ProgramData\SpeechLive Helper\localconfig.ini";
    public static string ProcessCompletePath = @"C:\ProgramData\SpeechLive Helper\EnglishCopyComplete.wav";
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

    /// <summary>
    /// Extract a device ID from the given input string.
    /// A device ID is either a string of 12 digits following '@' or '%'.
    /// If no valid device ID is found, returns "Unknown".
    /// </summary>
    /// <param name="input">Input string to extract from.</param>
    /// <returns>Device ID or "Unknown" if not found.</returns>
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

    /// <summary>
    /// Extract a device ID from the given input string.
    /// A device ID is a string of 12 digits.
    /// If no valid device ID is found, returns "Unknown".
    /// </summary>
    /// <param name="str">Input string to extract from.</param>
    /// <returns>Device ID or "Unknown" if not found.</returns>
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

    /// <summary>
    /// Extract the first field with the given label from the given input string.
    /// The label must be a word (i.e. it must not contain any whitespace or word characters).
    /// The extracted field is the first non-whitespace character sequence after the label.
    /// If no field with the given label is found, returns null.
    /// </summary>
    /// <param name="input">Input string to extract from.</param>
    /// <param name="label">Label of the field to extract.</param>
    /// <returns>Extracted field or null if not found.</returns>
    private static string ExtractField(string input, string label)
    {
        var regex = new Regex($@"(?<!\w){Regex.Escape(label)}\W*(\S+)");
        var match = regex.Match(input);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Extracts the metadata from a given file.
    /// Metadata is contained in the first 900 bytes of the file.
    /// The extracted metadata is returned in a <see cref="Metadata"/> object.
    /// </summary>
    /// <param name="path">Path to the file to extract metadata from.</param>
    /// <returns>Metadata object containing the extracted metadata.</returns>
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

