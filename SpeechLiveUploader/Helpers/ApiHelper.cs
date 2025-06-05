using System.Net;
using System.Net.Http.Headers;
using EncryptionService;

public class ApiHelper
{
    public async Task<HttpResponseMessage> PostHistoryAsync(string authorId, string priority, string worktype, string deviceId, string filePath)
    {
        // Create form
        var form = new MultipartFormDataContent();

        // Read the file bytes
        byte[] fileBytes = File.ReadAllBytes(filePath);
        
        // Check priority at offset 69
        string apiPriority = "0"; // Default priority
        if (fileBytes.Length > 69)
        {
            byte priorityByte = fileBytes[69];
            if (priorityByte == 0x08)
            {
                apiPriority = "8";
            }
            // If priorityByte is 0x00, we keep the default "0"
        }

        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath);
        string fileName = nameWithoutExtension + "-[" + LocalConfig.APP_Identifier + "]" + extension;
        form.Add(fileContent, "dictationFile", fileName);

        form.Add(new StringContent(apiPriority), "priority");
        form.Add(new StringContent(worktype), "worktype");

        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(300);

        // Decrypt the API key before using it
        string decryptedApiKey = EncryptionHelper.DecryptApiKey(CentralConfig.API_Bearer);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + decryptedApiKey);
        
        client.DefaultRequestHeaders.Add("App-Name", "SpeechLive Upload Helper");
        client.DefaultRequestHeaders.Add("App-Version", "1.1");
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

