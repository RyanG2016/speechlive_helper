using System.Security.Cryptography;

namespace EncryptionService;

/// <summary>
/// Provides methods for encrypting and decrypting API keys using AES-256 encryption.
/// </summary>
public static class EncryptionHelper
{
    /// <summary>
    /// The hardcoded secret key used for encryption/decryption.
    /// This key is used with PBKDF2 to derive the actual AES key and IV.
    /// </summary>
    private const string SecretKey = "SpeechLiveSecretKey2024";

    /// <summary>
    /// Encrypts the given plain text API key using AES encryption.
    /// </summary>
    /// <param name="plainText">The plain text API key to encrypt.</param>
    /// <returns>The encrypted API key as a Base64 string.</returns>
    public static string EncryptApiKey(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            using (Aes aes = Aes.Create())
            {
                // Derive key and IV from the secret key using PBKDF2
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
    /// <param name="cipherText">The encrypted API key (Base64 string) to decrypt.</param>
    /// <returns>The decrypted API key.</returns>
    public static string DecryptApiKey(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return string.Empty;

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);

            using (Aes aes = Aes.Create())
            {
                // Derive key and IV from the secret key using PBKDF2
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
            throw new Exception("Decryption failed. The encrypted text may be corrupted or was encrypted with a different key.", ex);
        }
        catch (Exception ex)
        {
            throw new Exception($"Decryption failed: {ex.Message}", ex);
        }
    }
}
