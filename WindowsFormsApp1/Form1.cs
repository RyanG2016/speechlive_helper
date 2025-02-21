using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using static System.Net.WebRequestMethods;
using File = System.IO.File;

namespace WindowsFormsApp1
{
    public partial class Form1 : Form
    {
        private static readonly Regex sWhitespace = new Regex(@"\s+");
        public static string ReplaceWhitespace(string input, string replacement)
        {
            return sWhitespace.Replace(input, replacement);
        }
        public Form1()
        {
            InitializeComponent();
        }

        private async void button1_Click(object sender, EventArgs e)
        {
            string path = @"I:\\Speech Live Files\Ryan101.DS2";
            const int HEADER_SIZE = 900;

            byte[] bytesFile = new byte[HEADER_SIZE];

            using (FileStream fs = File.OpenRead(path))
            {
                fs.Read(bytesFile, 0, HEADER_SIZE);
                fs.Close();
            }

            string hexValues = BitConverter.ToString(bytesFile);

            //string[] header = hex.Split(new Char[] { '-' }).ToArray();
            //textBox1.Text = System.String.Join("", header);

            //byte[] data = FromHex(hex);
            //textBox1.Text = Encoding.ASCII.GetString(data);


            string[] hexValuesSplit = hexValues.Split('-');
            string Info = "";
            foreach (string hex in hexValuesSplit)
            {
                // Convert the number expressed in base-16 to an integer.
                int value = Convert.ToInt32(hex, 16);
                // Get the character corresponding to the integral value.
                string stringValue = Char.ConvertFromUtf32(value);
                char charValue = (char)value;
                //if (hex != "00")
                //{
                if (hex != "FF")
                {
                    //textBox1.Text +=   string.Format("hexadecimal value = {0}, int value = {1}, char value = {2} or {3}",
                    //                    hex, value, stringValue, charValue);
                    if (hex == "00")
                        Info += " ";
                    else
                        Info += stringValue;



                }
                //}
            }

            textBox1.Text = ReplaceWhitespace(Info, " ");
            string Aut = "Author";
            string WT = "Work type";
            string aid = "";
            string did = "";
            string worktype = "";
            var regex = new Regex(string.Format(@"(?<!\w){0}\W+(\w+)", Regex.Escape(Aut)));
            var match = regex.Match(textBox1.Text);
            if (match.Success)
            {
                textBox1.Text += "Author : " + match.Groups[1].Value;
                aid = match.Groups[1].Value;
            }

            regex = new Regex(string.Format(@"(?<!\w){0}\W+(\w+)", Regex.Escape(WT)));

            var match1 = regex.Match(textBox1.Text);
            if (match1.Success)
            {
                textBox1.Text += " Work Type : " + match1.Groups[1].Value;
                worktype = match1.Groups[1].Value;
            }

            string str = textBox1.Text.Replace("@", "DevId: ");
            regex = new Regex(string.Format(@"(?<!\w){0}\W+(\w+)", Regex.Escape("DevId: ")));

            var match11 = regex.Match(str);
            if (match11.Success)
            {
                textBox1.Text += " Device ID : " + match11.Groups[1].Value;
                did = match11.Groups[1].Value;
            }

            var res = await PostHistoryAsync("psp_author2@vtexvsi.com", "0", worktype, did, @"I:\\Speech Live Files\Ryan101.DS2");
        }

        public static byte[] FromHex(string hex)
        {
            hex = hex.Replace("-", "");
            byte[] raw = new byte[hex.Length / 2];
            for (int i = 0; i < raw.Length; i++)
            {
                raw[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return raw;
        }

        private void button3_Click(object sender, EventArgs e)
        {
            string path = @"F:\\Speech Live Files\EnglishCopyComplete.wav";
            SoundPlayer my_wave_file = new SoundPlayer(path);
            my_wave_file.PlaySync();
        }


        private void button2_Click(object sender, EventArgs e)
        {
        }

        public async Task<string> PostHistoryAsync(string authorId, string priority, string worktype, string deviceId, string filePath )
        {

            string res = "";
            
            //Create form
            var form = new MultipartFormDataContent();
            //if (image != null)
            //{
            //    var bytefile = ImageToByteArray(image);
            byte[] fileBytes = File.ReadAllBytes(filePath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
            form.Add(fileContent, "dictationFile", filePath);
            //}

            form.Add(new StringContent(priority), "priority");
            form.Add(new StringContent(worktype), "worktype");
            
            var client = new HttpClient();

            client.Timeout = TimeSpan.FromSeconds(300);
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
            client.DefaultRequestHeaders.Add("App-Name", "SpeechLive Upload Helper");
            client.DefaultRequestHeaders.Add("App-Version", "0.1");
            client.DefaultRequestHeaders.Add("Device-Id", "Device UUID");
            client.DefaultRequestHeaders.Add("SL-User", "psp_author2@vtexvsi.com");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; AcmeInc/1.0)");

            try
            {
                var response = await client.PostAsync("https://sl-api-gateway-apim-prod-cae.azure-api.net/dictations", form);
                response.EnsureSuccessStatusCode();
                var responseContent = await response.Content.ReadAsStringAsync();
                res = responseContent;
          
            }
            catch (Exception ex)
            {
                
            }
            return res;
        }


        string token = "eyJhbGciOiJIUzI1NiIsImtpZCI6IkFkbWluQWNjZXNzVG9rZW4iLCJ0eXAiOiJKV1QifQ.eyJzbFN1YnNjcmlwdGlvbklkIjoiMTAyNDU3Iiwic2xSZWdpb24iOiJjYSIsImVtYWlsIjoicmdhdWRldEB2dGV4dnNpLmNvbSIsInNsVG9rZW5WZXIiOiIyLjAiLCJzbENsaWVudCI6InNsdjIiLCJyb2xlIjoiIiwianRpIjoiMnhoTENYZ1RoSDJVcWJhSEtLMllVTWFQTlFhWmt4dS9RUC93Mk1wYVdKVmIrTVpZWWpGYXRNdUl4dlk9IiwidG9rZW5JZCI6Ijk2MjQiLCJleHAiOjE3NzE1NDU2MDAsImlhdCI6MTc0MDA2NDMzMCwiaXNzIjoiU0wuQWRtaW4uQWNjZXNzIiwiYXVkIjoiU0wuVGhpcmRQYXJ0eSJ9.J8qV20WJSLCl_OQKNrqAICdygBVDZop-Aa9aqudQnpA";
    }
}
