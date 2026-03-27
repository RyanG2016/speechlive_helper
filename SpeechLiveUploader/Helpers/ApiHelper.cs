using System.Net;
using System.Net.Http.Headers;
using EncryptionService;

public class ApiHelper
{
    /// <summary>
    /// Gets the current log file path based on today's date.
    /// </summary>
    private string GetCurrentLogPath()
    {
        string logsFolderPath = ManageFiles.LogsFolderPath;
        string fileName = DateTime.Now.Date.ToString("MMddyyyy") + "_Logfile.txt";
        return Path.Combine(logsFolderPath, fileName);
    }

    public async Task<HttpResponseMessage> PostHistoryAsync(string authorId, string priority, string worktype, string deviceId, string filePath)
    {
        // Store the extracted worktype for logging
        string extractedWorktype = worktype;

        // Apply default worktype if empty
        if (string.IsNullOrEmpty(worktype))
        {
            worktype = "OFFICE VISIT";
            extractedWorktype = "(empty)";
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} INFO: WorkType was empty, using default: '{worktype}'");
        }

        // Check if static worktype should be used (Philips API now requires valid worktype)
        if (LocalConfig.USE_STATIC_WORKTYPE?.ToLower() == "true" && !string.IsNullOrEmpty(LocalConfig.STATIC_WORKTYPE))
        {
            string staticWorktype = LocalConfig.STATIC_WORKTYPE;
            if (worktype != staticWorktype)
            {
                ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} INFO: Using static worktype '{staticWorktype}' (extracted worktype was '{extractedWorktype}')");
                worktype = staticWorktype;
            }
        }

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

        // Check dictatedDate at offset 38-50
        string dictatedDate = ""; // Default empty
        if (fileBytes.Length > 50)
        {
            try
            {
                // Extract bytes from offset 38 to 50 (13 bytes total)
                byte[] dateBytes = new byte[13];
                Array.Copy(fileBytes, 38, dateBytes, 0, 13);

                // Convert to string and trim null characters
                string rawDateString = System.Text.Encoding.UTF8.GetString(dateBytes).TrimEnd('\0');

                // Check if it's a 12-digit packed date format (YYMMDDHHMMSS)
                if (rawDateString.Length >= 12 && rawDateString.All(char.IsDigit))
                {
                    // Parse as packed format: YYMMDDHHMMSS
                    string yearStr = rawDateString.Substring(0, 2);
                    string monthStr = rawDateString.Substring(2, 2);
                    string dayStr = rawDateString.Substring(4, 2);
                    string hourStr = rawDateString.Substring(6, 2);
                    string minuteStr = rawDateString.Substring(8, 2);
                    string secondStr = rawDateString.Substring(10, 2);

                    // Convert to integers
                    int year = 2000 + int.Parse(yearStr);
                    int month = int.Parse(monthStr);
                    int day = int.Parse(dayStr);
                    int hour = int.Parse(hourStr);
                    int minute = int.Parse(minuteStr);
                    int second = int.Parse(secondStr);

                    DateTime parsedDate = new DateTime(year, month, day, hour, minute, second);
                    dictatedDate = parsedDate.ToString("MMM d, yyyy, h:mm tt");
                }
                else
                {
                    // Try parsing as regular date string
                    if (DateTime.TryParse(rawDateString, out DateTime parsedDate))
                    {
                        dictatedDate = parsedDate.ToString("MMM d, yyyy, h:mm tt");
                    }
                }
            }
            catch (Exception)
            {
                // Handle any parsing errors
                dictatedDate = "";
            }
        }

        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath);
        string fileName = nameWithoutExtension + "-[" + LocalConfig.APP_Identifier + "]" + extension;
        form.Add(fileContent, "dictationFile", fileName);

        form.Add(new StringContent(apiPriority), "priority");
        form.Add(new StringContent(worktype), "worktype");
        if (!string.IsNullOrEmpty(dictatedDate))
        {
            form.Add(new StringContent(dictatedDate), "custom5");
        }

        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(300);

        // Decrypt the API key before using it
        string decryptedApiKey;
        try
        {
            decryptedApiKey = EncryptionHelper.DecryptApiKey(CentralConfig.API_Bearer);
        }
        catch (FileNotFoundException ex)
        {
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} CRITICAL ERROR: Cannot decrypt API key - secrets.json not found");
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} ERROR: {ex.Message}");
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} ACTION REQUIRED: Ensure secrets.json exists in C:\\ProgramData\\SpeechLive Helper\\");
            throw new InvalidOperationException("API key decryption failed - secrets.json not found. See logs for details.", ex);
        }
        catch (Exception ex)
        {
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} CRITICAL ERROR: Failed to decrypt API key");
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} ERROR: {ex.Message}");
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} POSSIBLE CAUSE: secrets.json may contain a different encryption key than was used to encrypt the API token");
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} ACTION REQUIRED: Re-encrypt the API key using ApiKeyEncryptor with the correct secrets.json");
            throw new InvalidOperationException("API key decryption failed. The encryption key may not match. See logs for details.", ex);
        }
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + decryptedApiKey);
        
        client.DefaultRequestHeaders.Add("App-Name", "SpeechLive Upload Helper");
        client.DefaultRequestHeaders.Add("App-Version", "1.1");
        client.DefaultRequestHeaders.Add("Device-Id", deviceId);
        client.DefaultRequestHeaders.Add("SL-User", authorId);
        client.DefaultRequestHeaders.Add("User-Agent", CentralConfig.API_UserAgent);



        try
        {
            // Log request details
            string endpoint = CentralConfig.API_Tenant + "/dictations";
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} DEBUG: Sending POST request to {endpoint}");
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} DEBUG: Request Headers - SL-User: {authorId}, Device-Id: {deviceId}");
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} DEBUG: Form Data - Priority: {apiPriority}, WorkType: '{worktype}', DictatedDate: '{dictatedDate}'");

            // Send the request and return the response
            var response = await client.PostAsync(endpoint, form);

            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} DEBUG: Response received with status: {response.StatusCode}");

            return response;
        }
        catch (Exception ex)
        {
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} ERROR: Exception in PostHistoryAsync");
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} Exception Type: {ex.GetType().Name}");
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} Exception Message: {ex.Message}");
            ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} Stack Trace: {ex.StackTrace}");

            if (ex.InnerException != null)
            {
                ManageFiles.CreateAndAppendLogs(GetCurrentLogPath(), $"{DateTime.Now} Inner Exception: {ex.InnerException.Message}");
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"Error occurred: {ex.Message} | Type: {ex.GetType().Name}")
            };
        }
    }


}

