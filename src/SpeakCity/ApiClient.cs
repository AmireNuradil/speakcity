using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SpeakCity;

/// <summary>
/// Sends transcript text only to the configured HTTPS chat-completions endpoint.
/// Keys belong in request headers, never in URLs, payloads, diagnostics, or error messages.
/// </summary>
public sealed class ApiClient
{
    private const int MaxResponseBytes = 256 * 1024;
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxHistoryMessages = 256;
    private const int MaxPromptCharacters = 256 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };
    private static readonly HttpClient Transport = CreateTransport();
    private readonly Func<ApiConfig> _config;

    public ApiClient(Func<ApiConfig> config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// History roles must be user or assistant. There is one initial system message.
    /// Output is strictly a JSON object; malformed, refused, or truncated replies are errors.
    /// </summary>
    public async Task<JsonObject> CompleteJsonAsync(
        string systemPrompt,
        IReadOnlyList<(string Role, string Content)> messages,
        int maxTokens,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var config = ReadConfigurationSnapshot();
        var history = ValidateAndCopyHistory(systemPrompt, messages);
        var tokenLimit = Math.Clamp(maxTokens, 16, 4096);
        // The supplied base already includes its version/path (for example /v1).
        // Do not insert /v1, and do not let a leading slash discard that supplied path.
        var endpoint = new Uri(config.BaseUrl + "/chat/completions", UriKind.Absolute);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        var requestToken = timeout.Token;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                requestToken.ThrowIfCancellationRequested();
                var requestObject = BuildRequest(config.Model, systemPrompt, history, tokenLimit,
                    includeResponseFormat: attempt == 0);
                var requestBytes = JsonSerializer.SerializeToUtf8Bytes(requestObject);
                byte[]? responseBytes = null;
                try
                {
                    if (requestBytes.Length > MaxRequestBytes)
                        throw new InvalidOperationException("The conversation is too large to send. Start a shorter conversation and try again.");

                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                    // Never use shared DefaultRequestHeaders: settings may change between calls.
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    request.Content = new ByteArrayContent(requestBytes);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                    using var response = await Transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        requestToken).ConfigureAwait(false);

                    var status = (int)response.StatusCode;
                    if (status is >= 300 and < 400)
                        throw new InvalidOperationException(
                            "The API endpoint tried to redirect the request. Redirects are blocked to protect your API key. Enter the provider's final HTTPS API base URL.");

                    // Only a structured 400/422 error can trigger the one compatibility retry.
                    // Authentication, quota, and server error bodies are never needed or exposed.
                    if (!response.IsSuccessStatusCode && status is not (400 or 422))
                        throw CreateStatusException(response.StatusCode);

                    responseBytes = await ReadBoundedBodyAsync(response.Content, requestToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (attempt == 0 && ExplicitlyRejectsResponseFormat(responseBytes))
                            continue;
                        throw CreateStatusException(response.StatusCode);
                    }

                    var result = ParseCompletion(responseBytes);
                    requestToken.ThrowIfCancellationRequested();
                    return result;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(requestBytes);
                    if (responseBytes is not null)
                        CryptographicOperations.ZeroMemory(responseBytes);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("The API request was canceled.", ct);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The API request timed out. Check your internet connection or try again when the provider is responding.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(
                "Could not reach the API securely. Check your internet connection, the configured HTTPS base URL, and your provider's service status.");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("The API connection was interrupted. Check your internet connection and try again.");
        }
        catch (AuthenticationException)
        {
            throw new InvalidOperationException("The API's secure connection could not be verified. Check the HTTPS base URL and your Windows clock; do not disable certificate checks.");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The API request settings are not in a supported format. Check the base URL, model ID, and API key.");
        }
        catch (JsonException)
        {
            // JsonException messages can contain provider-controlled text. Never surface them.
            throw new InvalidOperationException("The provider returned malformed JSON. Check that the endpoint and model support chat completions with JSON output.");
        }
        finally
        {
            // Do not retain the per-call key in a completed async state machine.
            config.ApiKey = "";
        }

        throw new InvalidOperationException("The provider could not complete a compatible JSON request. Check your API settings.");
    }

    /// <summary>Sends only a neutral synthetic probe, never a conversation or learner details.</summary>
    public async Task TestAsync(CancellationToken ct)
    {
        var response = await CompleteJsonAsync(
            "This is a connection test. Return only this JSON object: {\"ok\":true}.",
            new (string Role, string Content)[] { ("user", "Connection test. Reply with the requested JSON object only.") },
            maxTokens: 32,
            ct).ConfigureAwait(false);
        if (response.Count != 1 || response["ok"] is not JsonValue value ||
            !value.TryGetValue<bool>(out var ok) || !ok)
            throw new InvalidOperationException("The provider responded, but did not return the expected test JSON. Check that the chosen model supports instruction-following and JSON replies.");
    }

    private ApiConfig ReadConfigurationSnapshot()
    {
        ApiConfig? current;
        try
        {
            current = _config();
        }
        catch
        {
            // A caller-supplied settings callback must not leak an arbitrary exception or key.
            throw new InvalidOperationException("API settings could not be read. Open API settings and save a valid configuration.");
        }
        if (current is null)
            throw new InvalidOperationException("API configuration is required. Enter your provider's base URL, model ID, and API key in API settings.");
        return ApiConfigStore.NormalizeAndValidate(current);
    }

    private static (string Role, string Content)[] ValidateAndCopyHistory(
        string systemPrompt, IReadOnlyList<(string Role, string Content)> messages)
    {
        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new ArgumentException("A nonempty system prompt is required.");
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count > MaxHistoryMessages || systemPrompt.Length > MaxPromptCharacters)
            throw new ArgumentException("The conversation is too large to send. Start a shorter conversation and try again.");
        var result = new (string Role, string Content)[messages.Count];
        var totalCharacters = systemPrompt.Length;
        for (var i = 0; i < result.Length; i++)
        {
            var message = messages[i];
            if (message.Role is not ("user" or "assistant"))
                throw new ArgumentException("Conversation history may contain only user and assistant messages.");
            if (string.IsNullOrWhiteSpace(message.Content))
                throw new ArgumentException("Conversation messages must contain text.");
            if (message.Content.Length > MaxPromptCharacters - totalCharacters)
                throw new ArgumentException("The conversation is too large to send. Start a shorter conversation and try again.");
            totalCharacters += message.Content.Length;
            result[i] = message;
        }
        return result;
    }

    private static JsonObject BuildRequest(string model, string systemPrompt,
        IReadOnlyList<(string Role, string Content)> history, int maxTokens, bool includeResponseFormat)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt }
        };
        foreach (var message in history)
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });

        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.2,
            ["stream"] = false
        };
        if (includeResponseFormat)
            request["response_format"] = new JsonObject { ["type"] = "json_object" };
        return request;
    }

    private static HttpClient CreateTransport()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxResponseHeadersLength = 32
        };
        // Keep normal Windows TLS certificate verification. Never install a bypass callback.
        // ResponseHeadersRead plus the bounded reader, not this property alone, caps bodies.
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes
        };
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidOperationException("The provider's response exceeded the 256 KB safety limit. Ask for a shorter reply or use a smaller token limit.");

        // The extra byte detects overflow, including chunked or decompressed responses.
        var buffer = new byte[MaxResponseBytes + 1];
        try
        {
            using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var length = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), ct).ConfigureAwait(false);
                if (read == 0)
                    return buffer.AsSpan(0, length).ToArray();
                length += read;
                if (length > MaxResponseBytes)
                    throw new InvalidOperationException("The provider's response exceeded the 256 KB safety limit. Ask for a shorter reply or use a smaller token limit.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static bool ExplicitlyRejectsResponseFormat(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var error = root.TryGetProperty("error", out var nested) ? nested : root;
            if (error.ValueKind != JsonValueKind.Object && error.ValueKind != JsonValueKind.String)
                return false;

            var parameter = ReadErrorString(error, "param");
            var code = ReadErrorString(error, "code");
            var message = error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? ""
                : ReadErrorString(error, "message");
            if (message.Length > 4096)
                return false;

            var parameterNamesFormat = parameter.Equals("response_format", StringComparison.OrdinalIgnoreCase) ||
                                       parameter.StartsWith("response_format.", StringComparison.OrdinalIgnoreCase) ||
                                       parameter.StartsWith("response_format[", StringComparison.OrdinalIgnoreCase);
            // A rejection explicitly attributed to another parameter is not a format retry,
            // even if the provider echoes response_format elsewhere in its error message.
            if (parameter.Length != 0 && !parameterNamesFormat)
                return false;
            var mentionsFormat = parameterNamesFormat ||
                                 message.Contains("response_format", StringComparison.OrdinalIgnoreCase) ||
                                 message.Contains("json_object", StringComparison.OrdinalIgnoreCase) ||
                                 message.Contains("json mode", StringComparison.OrdinalIgnoreCase);
            if (!mentionsFormat)
                return false;

            var unsupportedCode = code.Equals("unsupported_parameter", StringComparison.OrdinalIgnoreCase) ||
                                  code.Equals("unsupported_value", StringComparison.OrdinalIgnoreCase) ||
                                  code.Equals("unknown_parameter", StringComparison.OrdinalIgnoreCase) ||
                                  code.Equals("unrecognized_parameter", StringComparison.OrdinalIgnoreCase) ||
                                  code.Equals("parameter_not_supported", StringComparison.OrdinalIgnoreCase);
            if (parameterNamesFormat && unsupportedCode)
                return true;

            string[] rejectionPhrases =
            {
                "unsupported", "not supported", "does not support", "doesn't support", "not available",
                "not allowed", "unknown parameter", "unrecognized parameter", "unrecognised parameter",
                "unrecognized request argument", "unrecognised request argument", "unexpected keyword argument",
                "unknown field", "unrecognized field", "is not a valid parameter", "unexpected parameter",
                "extra inputs are not permitted", "not permitted", "only supports"
            };
            foreach (var phrase in rejectionPhrases)
            {
                if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (JsonException)
        {
            // An HTML/text/generic error is not explicit evidence of a format incompatibility.
            return false;
        }
    }

    private static string ReadErrorString(JsonElement error, string name)
    {
        if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
            return "";
        var text = value.GetString() ?? "";
        return text.Length <= 4096 ? text : "";
    }

    private static JsonObject ParseCompletion(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, JsonOptions);
        var envelope = document.RootElement;
        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() != 1)
            throw InvalidReply();

        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object)
            throw InvalidReply();
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
        {
            if (finish.ValueKind != JsonValueKind.String)
                throw InvalidReply();
            var reason = finish.GetString();
            if (reason == "length")
                throw new InvalidOperationException("The provider stopped before finishing its JSON reply. Ask for a shorter reply or increase the token limit.");
            if (reason == "content_filter")
                throw new InvalidOperationException("The provider declined this request under its content policy. Try a different, appropriate prompt.");
            if (reason != "stop")
                throw InvalidReply();
        }
        if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            throw InvalidReply();
        if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null &&
            (refusal.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(refusal.GetString())))
            throw new InvalidOperationException("The provider declined to return a reply. Try a different, appropriate prompt.");
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw InvalidReply();

        var text = UnwrapJsonFence(content.GetString() ?? "");
        if (text.Length == 0)
            throw InvalidReply();
        using var replyDocument = JsonDocument.Parse(text, JsonOptions);
        if (replyDocument.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The provider returned JSON that was not an object. The selected model must return a JSON object, not a list or plain text.");
        RejectDuplicateProperties(replyDocument.RootElement);
        return JsonNode.Parse(text, documentOptions: JsonOptions) as JsonObject ?? throw InvalidReply();
    }

    private static string UnwrapJsonFence(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;
        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0 || !text.EndsWith("```", StringComparison.Ordinal))
            throw InvalidReply();
        var label = text[3..firstLineEnd].Trim();
        if (label.Length != 0 && !label.Equals("json", StringComparison.OrdinalIgnoreCase))
            throw InvalidReply();
        var closingFence = text.Length - 3;
        if (closingFence <= firstLineEnd || text[closingFence - 1] != '\n')
            throw InvalidReply();
        return text[(firstLineEnd + 1)..closingFence].Trim();
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidOperationException("The provider returned ambiguous JSON with duplicate property names. Try again.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static InvalidOperationException InvalidReply() => new(
        "The provider did not return a complete chat-completions JSON reply. Check that the configured endpoint and model support JSON objects; no substitute reply was generated.");

    private static Exception CreateStatusException(HttpStatusCode code) => (int)code switch
    {
        400 or 422 => new InvalidOperationException("The provider rejected the request. Check the exact API base URL, model ID, and chat-completions compatibility."),
        401 => new InvalidOperationException("The provider did not accept the API key (401). Check or replace it in API settings; never paste keys into chat."),
        403 => new InvalidOperationException("The API key is not permitted to use this endpoint or model (403). Check the key's permissions and whether the model is enabled for your account."),
        404 => new InvalidOperationException("The API endpoint or model was not found (404). Check the provider's exact base URL and model ID."),
        408 or 504 => new TimeoutException("The provider timed out. Check your internet connection or try again later."),
        413 => new InvalidOperationException("The provider rejected the request as too large (413). Start a shorter conversation and try again."),
        429 => new InvalidOperationException("The provider's rate or usage limit was reached (429). Wait before retrying, or check your account quota and billing with the provider."),
        >= 500 => new InvalidOperationException("The provider is temporarily unavailable. Try again later or check its service status."),
        _ => new InvalidOperationException($"The provider returned HTTP {(int)code}. Check your API settings and the provider's chat-completions documentation.")
    };
}
