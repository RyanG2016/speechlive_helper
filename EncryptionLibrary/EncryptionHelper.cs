using System.Security.Cryptography;
using System.Text.Json;

namespace EncryptionService;

/// <summary>
/// Provides methods for encrypting and decrypting API keys.
/// </summary>
public static class EncryptionHelper
{
    /// <summary>
    /// The name of the secrets file containing the encryption key.
    /// </summary>
    public const string SecretsFileName = "secrets.json";

    /// <summary>
    /// The cached encryption key (loaded once from secrets.json).
    /// </summary>
    private static string? _cachedSecretKey;

    /// <summary>
    /// Gets the secret key from secrets.json. Throws if not found or invalid.
    /// For SpeechLiveUploader service, looks in ProgramData folder.
    /// For ApiKeyEncryptor, looks in the application directory.
    /// </summary>
    private static string GetSecretKey()
    {
        if (_cachedSecretKey != null)
            return _cachedSecretKey;

        // Try multiple locations in order of priority
        var searchPaths = new[]
        {
            // 1. Application directory (for ApiKeyEncryptor)
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SecretsFileName),
            // 2. ProgramData folder (for SpeechLiveUploader service)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpeechLive Helper", SecretsFileName)
        };

        foreach (var path in searchPaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var secrets = JsonSerializer.Deserialize<SecretsFile>(json);

                    if (secrets == null || string.IsNullOrWhiteSpace(secrets.EncryptionKey))
                    {
                        throw new InvalidOperationException($"secrets.json at '{path}' is invalid or missing EncryptionKey.");
                    }

                    _cachedSecretKey = secrets.EncryptionKey;
                    return _cachedSecretKey;
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"Failed to parse secrets.json at '{path}': {ex.Message}", ex);
                }
            }
        }

        throw new FileNotFoundException(
            $"Encryption key not found. Please ensure '{SecretsFileName}' exists in one of these locations:\n" +
            string.Join("\n", searchPaths) +
            "\n\nUse the ApiKeyEncryptor tool to generate a new secrets.json file.");
    }

    /// <summary>
    /// Generates a new random encryption key.
    /// </summary>
    /// <returns>A cryptographically secure random key.</returns>
    public static string GenerateNewKey()
    {
        // Generate a 32-byte (256-bit) random key and encode as Base64
        byte[] keyBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(keyBytes);
        }
        return Convert.ToBase64String(keyBytes);
    }

    /// <summary>
    /// Saves the encryption key to a secrets.json file.
    /// </summary>
    /// <param name="key">The encryption key to save.</param>
    /// <param name="filePath">The path to save the secrets.json file.</param>
    public static void SaveSecretsFile(string key, string filePath)
    {
        var secrets = new SecretsFile { EncryptionKey = key };
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(secrets, options);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Loads the encryption key from a secrets.json file.
    /// </summary>
    /// <param name="filePath">The path to the secrets.json file.</param>
    /// <returns>The encryption key, or null if the file doesn't exist or is invalid.</returns>
    public static string? LoadSecretsFile(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = File.ReadAllText(filePath);
            var secrets = JsonSerializer.Deserialize<SecretsFile>(json);
            return secrets?.EncryptionKey;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the default secrets file path for the current application.
    /// </summary>
    public static string GetDefaultSecretsPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SecretsFileName);
    }

    /// <summary>
    /// Checks if a secrets.json file exists in any of the search locations.
    /// </summary>
    public static bool SecretsFileExists()
    {
        var searchPaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SecretsFileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SpeechLive Helper", SecretsFileName)
        };

        return searchPaths.Any(File.Exists);
    }

    /// <summary>
    /// Encrypts the given plain text API key using AES encryption.
    /// </summary>
    /// <param name="plainText">The plain text API key to encrypt.</param>
    /// <returns>The encrypted API key.</returns>
    /// <exception cref="ArgumentException">The <paramref name="plainText"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The encryption key could not be loaded from secrets.json.</exception>
    /// <exception cref="Exception">Encryption failed due to an unexpected error.</exception>
    public static string EncryptApiKey(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        string secretKey = GetSecretKey();

        try
        {
            using (Aes aes = Aes.Create())
            {
                // Derive key and IV from the secret key
                using (var deriveBytes = new Rfc2898DeriveBytes(secretKey, new byte[8], 1000, HashAlgorithmName.SHA256))
                {
                    aes.Key = deriveBytes.GetBytes(32); // 256 bits
                    aes.IV = deriveBytes.GetBytes(16);  // 128 bits
                }

                ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);

                using (MemoryStream msEncrypt = new MemoryStream())
                {
                    using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
                    {
                        swEncrypt.Write(plainText);
                    }

                    return Convert.ToBase64String(msEncrypt.ToArray());
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw; // Re-throw key loading errors as-is
        }
        catch (Exception ex)
        {
            throw new Exception($"Encryption failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Decrypts the given encrypted API key using AES encryption.
    /// </summary>
    /// <param name="cipherText">The encrypted API key to decrypt.</param>
    /// <returns>The decrypted API key.</returns>
    /// <exception cref="ArgumentException">The <paramref name="cipherText"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">The encryption key could not be loaded from secrets.json.</exception>
    /// <exception cref="FormatException">The <paramref name="cipherText"/> is not a valid Base64 string.</exception>
    /// <exception cref="CryptographicException">The decryption failed. The encrypted text may be corrupted or was encrypted with different keys.</exception>
    /// <exception cref="Exception">Decryption failed due to an unexpected error.</exception>
    public static string DecryptApiKey(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;

        string secretKey = GetSecretKey();

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);

            using (Aes aes = Aes.Create())
            {
                // Derive key and IV from the secret key
                using (var deriveBytes = new Rfc2898DeriveBytes(secretKey, new byte[8], 1000, HashAlgorithmName.SHA256))
                {
                    aes.Key = deriveBytes.GetBytes(32); // 256 bits
                    aes.IV = deriveBytes.GetBytes(16);  // 128 bits
                }
                ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);

                using (MemoryStream msDecrypt = new MemoryStream(cipherBytes))
                using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                using (StreamReader srDecrypt = new StreamReader(csDecrypt))
                {
                    return srDecrypt.ReadToEnd();
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw; // Re-throw key loading errors as-is
        }
        catch (FormatException)
        {
            throw new Exception("Invalid encrypted text format. The text must be a valid Base64 string.");
        }
        catch (CryptographicException ex)
        {
            throw new Exception("Decryption failed. The encrypted text may be corrupted or was encrypted with a different encryption key. Ensure the same secrets.json is used for encryption and decryption.", ex);
        }
        catch (Exception ex)
        {
            throw new Exception($"Decryption failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Clears the cached secret key, forcing it to be reloaded on next use.
    /// </summary>
    public static void ClearCachedKey()
    {
        _cachedSecretKey = null;
    }
}

/// <summary>
/// Represents the structure of the secrets.json file.
/// </summary>
public class SecretsFile
{
    public string EncryptionKey { get; set; } = string.Empty;
}
