using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;


public class ApiHelper
{
    public async Task<HttpResponseMessage> PostHistoryAsync(string authorId, string priority, string worktype, string deviceId, string filePath)
    {
        // Create form
        var form = new MultipartFormDataContent();



        // Read the file bytes
        byte[] fileBytes = File.ReadAllBytes(filePath);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
        form.Add(fileContent, "dictationFile", filePath);



        form.Add(new StringContent(priority), "priority");
        form.Add(new StringContent(worktype), "worktype");



        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(300);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + CentralConfig.API_Bearer);
        client.DefaultRequestHeaders.Add("App-Name", "SpeechLive Upload Helper");
        client.DefaultRequestHeaders.Add("App-Version", "0.1");
        client.DefaultRequestHeaders.Add("Device-Id", deviceId);
        client.DefaultRequestHeaders.Add("SL-User", authorId);
        client.DefaultRequestHeaders.Add("User-Agent", CentralConfig.API_UserAgent);



        try
        {
            // Send the request and return the response
            var response = await client.PostAsync(CentralConfig.API_Tenant + "/dictations", form);
            return response;
        }
        catch (Exception ex)
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Error occurred: " + ex.Message)
            };
        }
    }


}

