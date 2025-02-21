using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;


public class ApiHelper
{
    string token = "eyJhbGciOiJIUzI1NiIsImtpZCI6IkFkbWluQWNjZXNzVG9rZW4iLCJ0eXAiOiJKV1QifQ.eyJzbFN1YnNjcmlwdGlvbklkIjoiMTAyNDU3Iiwic2xSZWdpb24iOiJjYSIsImVtYWlsIjoicmdhdWRldEB2dGV4dnNpLmNvbSIsInNsVG9rZW5WZXIiOiIyLjAiLCJzbENsaWVudCI6InNsdjIiLCJyb2xlIjoiIiwianRpIjoiTWFVeHRUMTN5aUZtSmdZSE4wYkNIQjJIcWJuczNyN200RlR3VGJBZVh1OE5hSTNxeUJJditXN1hEMGM9IiwidG9rZW5JZCI6Ijk2MTgiLCJleHAiOjE3Njg5NTM2MDAsImlhdCI6MTczNzQ3NTMzNCwiaXNzIjoiU0wuQWRtaW4uQWNjZXNzIiwiYXVkIjoiU0wuVGhpcmRQYXJ0eSJ9.k9vASL0rL248qTWZCIP6q3PnTYoOb1IX_wl-HXWUjl0";
    public async Task<string> PostHistoryAsync(string authorId, string priority, string worktype, string deviceId, string filePath)
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
        //client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; AcmeInc/1.0)");
        client.DefaultRequestHeaders.Add("User-Agent", CentralConfig.API_UserAgent);
        try
        {
            //var response = await client.PostAsync("https://sl-api-gateway-apim-prod-cae.azure-api.net/dictations", form);
            var response = await client.PostAsync(CentralConfig.API_Tenant + "/dictations", form);
            //response.EnsureSuccessStatusCode();
            var responseContent = await response.Content.ReadAsStringAsync();
            res = responseContent;

        }
        catch (Exception ex)
        {

        }
        return res;
    }

}

