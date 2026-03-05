using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;


public class IniFile
{
    string Path;
    string EXE = Assembly.GetExecutingAssembly().GetName().Name;

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern long WritePrivateProfileString(string Section, string Key, string Value, string FilePath);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern int GetPrivateProfileString(string Section, string Key, string Default, StringBuilder RetVal, int Size, string FilePath);

    public IniFile(string IniPath = null)
    {
        Path = new FileInfo(IniPath ?? EXE + ".ini").FullName;
    }

    public string Read(string Key, string Section = null)
    {
        var RetVal = new StringBuilder(2048);
        GetPrivateProfileString(Section ?? EXE, Key, "", RetVal, 2048, Path);
        return RetVal.ToString();
    }

    public void Write(string Key, string Value, string Section = null)
    {
        WritePrivateProfileString(Section ?? EXE, Key, Value, Path);
    }

    public void DeleteKey(string Key, string Section = null)
    {
        Write(Key, null, Section ?? EXE);
    }

    public void DeleteSection(string Section = null)
    {
        Write(null, null, Section ?? EXE);
    }

    public bool KeyExists(string Key, string Section = null)
    {
        return Read(Key, Section).Length > 0;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern int GetPrivateProfileSection(
    string lpAppName,
    byte[] lpszReturnBuffer,
    int nSize,
    string lpFileName);

    public List<string> GetUsers()
    {
        byte[] lpszReturnBuffer = new byte[2048];
        IniFile.GetPrivateProfileSection("Users", lpszReturnBuffer, 2048, this.Path);



        // Convert byte array to string and remove null characters
        string sectionData = Encoding.ASCII.GetString(lpszReturnBuffer).Trim('\0');


        sectionData = sectionData.Replace("\0\0", "\n");

        // Remove null characters that separate the entries, and split by new lines
        sectionData = sectionData.Replace("\0", "");



        // Split the section data by newlines to get individual user entries
        string[] userEntries = sectionData.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);



        List<string> users = new List<string>();



        // Iterate through each user entry
        foreach (var entry in userEntries)
        {
            // Split by '=' to separate user ID and email
            string[] userPair = entry.Split(new[] { '=' }, 2); // Limit split to two parts



            // Ensure there are exactly two parts (user ID and email)
            if (userPair.Length == 2)
            {
                // Add user in the format ID=email
                users.Add(userPair[0].Trim() + "=" + userPair[1].Trim());
            }
        }



        return users;
    }

    public string GetToken()
    {
        byte[] lpszReturnBuffer = new byte[2048];
        IniFile.GetPrivateProfileSection("Config", lpszReturnBuffer, 2048, this.Path);
        return Encoding.ASCII.GetString(lpszReturnBuffer).Trim(char.MinValue).Split(char.MinValue, (StringSplitOptions)0)[0];
    }
}

