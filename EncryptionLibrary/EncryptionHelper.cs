using System.Security.Cryptography;

namespace EncryptionService;

/// <summary>
/// Provides methods for encrypting and decrypting API keys.
/// </summary>
public static class EncryptionHelper
{
    /// <summary>
    /// The secret key used for encryption.
    /// </summary>
    private static readonly string SecretKey = "SpeechLiveSecretKey2024";

    /// <summary>
    /// Encrypts the given plain text API key using AES encryption.
    /// </summary>
    /// <param name="plainText">The plain text API key to encrypt.</param>
    /// <returns>The encrypted API key.</returns>
    /// <exception cref="ArgumentException">The <paramref name="plainText"/> is null or empty.</exception>
    /// <exception cref="Exception">Encryption failed due to an unexpected error.</exception>
    public static string EncryptApiKey(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            using (Aes aes = Aes.Create())
            {
                // Derive key and IV from the secret key
                using (var deriveBytes = new Rfc2898DeriveBytes(SecretKey, new byte[8], 1000, HashAlgorithmName.SHA256))
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
    /// <exception cref="FormatException">The <paramref name="cipherText"/> is not a valid Base64 string.</exception>
    /// <exception cref="CryptographicException">The decryption failed. The encrypted text may be corrupted or was encrypted with different keys.</exception>
    /// <exception cref="Exception">Decryption failed due to an unexpected error.</exception>
    public static string DecryptApiKey(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);

            using (Aes aes = Aes.Create())
            {
                // Derive key and IV from the secret key
                using (var deriveBytes = new Rfc2898DeriveBytes(SecretKey, new byte[8], 1000, HashAlgorithmName.SHA256))
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
        catch (FormatException)
        {
            throw new Exception("Invalid encrypted text format. The text must be a valid Base64 string.");
        }
        catch (CryptographicException ex)
        {
            throw new Exception("Decryption failed. The encrypted text may be corrupted or was encrypted with different keys.", ex);
        }
        catch (Exception ex)
        {
            throw new Exception($"Decryption failed: {ex.Message}", ex);
        }
    }
}
