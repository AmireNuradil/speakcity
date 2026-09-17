using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpeakCity;

public sealed class ApiConfig
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public static class ApiConfigStore
{
    internal const int MaxBaseUrlLength = 2048;
    internal const int MaxModelLength = 256;
    internal const int MaxApiKeyLength = 4096;
    private const int MaxSettingsBytes = 64 * 1024;
    private const int MaxPlaintextBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };

    /// <summary>Missing, corrupt, or unreadable settings require configuration again.</summary>
    public static ApiConfig Load()
    {
        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            using var stream = new FileStream(GetSettingsPath(), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaxSettingsBytes)
                return new ApiConfig();

            encrypted = new byte[(int)stream.Length];
            stream.ReadExactly(encrypted);
            if (stream.ReadByte() != -1)
                return new ApiConfig();

            plaintext = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
            if (plaintext.Length is <= 0 or > MaxPlaintextBytes)
                return new ApiConfig();

            var config = JsonSerializer.Deserialize<ApiConfig>(plaintext, JsonOptions);
            return config is null ? new ApiConfig() : NormalizeAndValidate(config);
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            // Never log decrypted settings or the underlying exception.
            return new ApiConfig();
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null)
                CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public static void Save(ApiConfig config)
    {
        var normalized = NormalizeAndValidate(config);
        byte[]? plaintext = null;
        byte[]? encrypted = null;
        string? temporaryPath = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
            if (plaintext.Length > MaxPlaintextBytes)
                throw new InvalidOperationException("API settings exceed the secure storage limit.");

            // Encryption happens before any settings bytes are written to disk.
            encrypted = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
            if (encrypted.Length > MaxSettingsBytes)
                throw new InvalidOperationException("API settings exceed the secure storage limit.");

            var destination = GetSettingsPath();
            var directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $"api-settings.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }

            // Same-directory replacement avoids truncating an existing working configuration.
            File.Move(temporaryPath, destination, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception error) when (IsStorageFailure(error))
        {
            // Do not attach an inner exception that could expose sensitive input or paths.
            throw new InvalidOperationException(
                "API settings could not be saved securely for this Windows user. Check local storage permissions and try again.");
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
            if (encrypted is not null)
                CryptographicOperations.ZeroMemory(encrypted);
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception error) when (IsStorageFailure(error)) { /* Only encrypted bytes could remain. */ }
            }
        }
    }

    public static bool IsConfigured(ApiConfig config)
    {
        if (config is null)
            return false;
        try
        {
            _ = NormalizeAndValidate(config);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static ApiConfig NormalizeAndValidate(ApiConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var baseUri = GetValidatedBaseUri(config.BaseUrl);
        var model = config.Model;
        if (string.IsNullOrWhiteSpace(model) || model.Length > MaxModelLength)
            throw new ArgumentException("Enter the exact model ID supplied by your provider (maximum 256 characters).");
        foreach (var character in model)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '/' or ':' or '@' or '+' or '~'))
                throw new ArgumentException("The model ID must not contain spaces, quotes, control characters, or other non-ID characters.");
        }

        var key = config.ApiKey;
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxApiKeyLength)
            throw new ArgumentException("Enter an API key for your chosen provider (maximum 4096 characters).");
        foreach (var character in key)
        {
            if (character < '!' || character > '~')
                throw new ArgumentException("The API key must contain visible ASCII characters only, without spaces, tabs, or line breaks.");
        }

        return new ApiConfig
        {
            BaseUrl = baseUri.AbsoluteUri.TrimEnd('/'),
            Model = model,
            ApiKey = key
        };
    }

    internal static Uri GetValidatedBaseUri(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Length > MaxBaseUrlLength)
            throw new ArgumentException("Enter your provider's HTTPS API base URL (maximum 2048 characters).");
        foreach (var character in baseUrl)
        {
            if (char.IsControl(character))
                throw new ArgumentException("The API base URL must not contain control characters or line breaks.");
        }
        var value = baseUrl.Trim();
        if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The API base URL must start with https://. Unencrypted HTTP is not allowed.");
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\')
                throw new ArgumentException("The API base URL must not contain spaces, control characters, or backslashes.");
        }
        if (value.Contains('?') || value.Contains('#'))
            throw new ArgumentException("Use an API base URL without a query string or fragment. Do not put an API key in the URL.");

        // Also reject empty userinfo (https://@host), which Uri.UserInfo reports as empty.
        var authorityEnd = value.IndexOf('/', "https://".Length);
        var authority = value.AsSpan("https://".Length,
            (authorityEnd < 0 ? value.Length : authorityEnd) - "https://".Length);
        if (authority.Contains('@'))
            throw new ArgumentException("The API base URL must not contain a username or password. Put the API key only in the API key field.");

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(uri.Host) || uri.HostNameType == UriHostNameType.Unknown || uri.Port <= 0 ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsoluteUri.Length > MaxBaseUrlLength)
            throw new ArgumentException("Enter a valid HTTPS API base URL with a valid host and port.");

        // Force IDN validation now, so neither the host preview nor a later request can
        // throw an exception containing malformed URL input.
        try { _ = uri.IdnHost; }
        catch (UriFormatException)
        {
            throw new ArgumentException("Enter a valid HTTPS API host name.");
        }
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter the API base URL, not /chat/completions. The application appends that endpoint automatically.");
        return uri;
    }

    private static string GetSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("Windows local application data is unavailable.");
        return Path.Combine(localAppData, "SpeakCity", "api-settings.bin");
    }

    private static bool IsStorageFailure(Exception error) => error is
        IOException or UnauthorizedAccessException or CryptographicException or JsonException or
        NotSupportedException or SecurityException or ArgumentException or InvalidOperationException;
}
