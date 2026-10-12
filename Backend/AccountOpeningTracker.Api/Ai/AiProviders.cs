using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AccountOpeningTracker.Api.Ai;

public sealed class AiOptions
{
    public const string Section = "Ai";

    /// <summary>Groq (default, free tier) | Ollama (local) | Anthropic | OpenAI | Disabled</summary>
    public string Provider { get; set; } = "Groq";
    public int TimeoutSeconds { get; set; } = 90;
    public int MaxOutputTokens { get; set; } = 900;
    public double Temperature { get; set; } = 0.2;

    public OpenAiOptions Groq { get; set; } = new()
    {
        BaseUrl = "https://api.groq.com/openai/v1",
        Model = "openai/gpt-oss-120b"
    };
    public OllamaOptions Ollama { get; set; } = new();
    public AnthropicOptions Anthropic { get; set; } = new();
    public OpenAiOptions OpenAI { get; set; } = new();

    public sealed class OllamaOptions
    {
        public string BaseUrl { get; set; } = "http://localhost:11434";
        public string Model { get; set; } = "qwen2.5:3b";
        public string VisionModel { get; set; } = "llava";
    }

    public sealed class AnthropicOptions
    {
        public string BaseUrl { get; set; } = "https://api.anthropic.com";
        /// <summary>Set via user-secrets or the Ai__Anthropic__ApiKey environment variable. Never commit.</summary>
        public string? ApiKey { get; set; }
        public string Model { get; set; } = "claude-sonnet-5-5";
        public string? VisionModel { get; set; }
    }

    public sealed class OpenAiOptions
    {
        /// <summary>Any OpenAI-compatible Chat Completions endpoint (also used for Groq).</summary>
        public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        public string? ApiKey { get; set; }
        public string Model { get; set; } = "gpt-4o-mini";
        /// <summary>Model used when an image is attached. Defaults to Model.</summary>
        public string? VisionModel { get; set; }
    }
}

public class AiUnavailableException : Exception
{
    public AiUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

public class AiOutputException : Exception
{
    public AiOutputException(string message) : base(message) { }
}

public sealed record AiHealth(string Provider, string Model, bool Configured, bool Reachable, bool External, string Message,
    string? VisionModel = null, bool? VisionAvailable = null, IReadOnlyList<string>? AvailableModels = null);

/// <summary>An image sent to a vision-capable model.</summary>
public sealed record AiImage(byte[] Data, string MimeType)
{
    public string Base64 => Convert.ToBase64String(Data);
    public string DataUrl => $"data:{MimeType};base64,{Base64}";
}

public interface IAiProvider
{
    string Name { get; }
    string Model { get; }
    string VisionModel { get; }
    /// <summary>True when data leaves this machine.</summary>
    bool IsExternal { get; }
    /// <summary>Returns the model's reply (expected to be a JSON object). Image is optional.</summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct, AiImage? image = null);
    Task<AiHealth> CheckAsync(CancellationToken ct);
}

public sealed class DisabledAiProvider : IAiProvider
{
    public string Name => "Disabled";
    public string Model => "-";
    public string VisionModel => "-";
    public bool IsExternal => false;

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct, AiImage? image = null) =>
        throw new AiUnavailableException("AI is disabled. Set Ai:Provider to Groq, Ollama, Anthropic or OpenAI.");

    public Task<AiHealth> CheckAsync(CancellationToken ct) =>
        Task.FromResult(new AiHealth(Name, Model, false, false, false, "AI is disabled in configuration (Ai:Provider)."));
}

internal static class AiHttp
{
    public static async Task<string> ReadOrThrowAsync(HttpResponseMessage response, string name, string configKey, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        int code = (int)response.StatusCode;
        if (response.IsSuccessStatusCode) return text;

        // Surface the provider's own error message (never contains our key).
        string detail = ProviderMessage(text);
        // Keep only the "try again in Ns" hint: the raw message names the account and links to billing.
        if (code == 429)
        {
            var wait = System.Text.RegularExpressions.Regex.Match(detail ?? "", @"try again in ([0-9.]+)s", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var hint = wait.Success && double.TryParse(wait.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs)
                ? $" Try again in about {Math.Ceiling(secs)} seconds."
                : " Wait a minute and try again.";
            throw new AiUnavailableException($"{name} free-tier rate limit reached.{hint}");
        }
        if (code is 401 or 403) throw new AiUnavailableException($"{name} rejected the API key (HTTP {code}). Check Ai:{configKey}:ApiKey. {detail}".Trim());
        if (code == 413) throw new AiUnavailableException("The request is too large for the AI provider.");
        throw new AiUnavailableException($"{name} returned HTTP {code}: {detail}".Trim());
    }

    private static string ProviderMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var msg = node?["error"]?["message"]?.GetValue<string>() ?? node?["error"]?.GetValue<string>() ?? node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(msg)) return msg.Length > 300 ? msg[..300] : msg;
        }
        catch (Exception) { /* not JSON */ }
        return body.Length > 200 ? body[..200] : body;
    }
}

/// <summary>Local Ollama server.</summary>
public sealed class OllamaProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;

    public OllamaProvider(HttpClient http, AiOptions options)
    {
        _http = http;
        _options = options;
    }

    public string Name => "Ollama";
    public string Model => _options.Ollama.Model;
    public string VisionModel => _options.Ollama.VisionModel;
    public bool IsExternal => false;

    private Uri Url(string path) => new(new Uri(_options.Ollama.BaseUrl.TrimEnd('/') + "/"), path);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct, AiImage? image = null)
    {
        var user = new JsonObject { ["role"] = "user", ["content"] = userPrompt };
        if (image != null) user["images"] = new JsonArray(JsonValue.Create(image.Base64));

        var body = new JsonObject
        {
            ["model"] = image != null ? VisionModel : Model,
            ["stream"] = false,
            ["format"] = "json",
            ["options"] = new JsonObject { ["temperature"] = _options.Temperature, ["num_predict"] = _options.MaxOutputTokens },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = systemPrompt }, user)
        };

        HttpResponseMessage response;
        try { response = await _http.PostAsync(Url("api/chat"), new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct); }
        catch (HttpRequestException ex) { throw new AiUnavailableException("Cannot reach the local Ollama server.", ex); }
        using (response)
        {
            var text = await AiHttp.ReadOrThrowAsync(response, Name, "Ollama", ct);
            return JsonNode.Parse(text)?["message"]?["content"]?.GetValue<string>()
                   ?? throw new AiOutputException("Ollama returned an empty response.");
        }
    }

    public async Task<AiHealth> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(Url("api/tags"), ct);
            return response.IsSuccessStatusCode
                ? new AiHealth(Name, Model, true, true, false, "Local Ollama server is running.")
                : new AiHealth(Name, Model, true, false, false, $"Ollama responded with HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AiHealth(Name, Model, true, false, false, $"Cannot reach Ollama at {_options.Ollama.BaseUrl}.");
        }
    }
}

/// <summary>Anthropic Messages API.</summary>
public sealed class AnthropicProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;

    public AnthropicProvider(HttpClient http, AiOptions options)
    {
        _http = http;
        _options = options;
    }

    public string Name => "Anthropic";
    public string Model => _options.Anthropic.Model;
    public string VisionModel => _options.Anthropic.VisionModel ?? Model;
    public bool IsExternal => true;

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _options.Anthropic.BaseUrl.TrimEnd('/') + path);
        request.Headers.Add("x-api-key", _options.Anthropic.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct, AiImage? image = null)
    {
        if (string.IsNullOrWhiteSpace(_options.Anthropic.ApiKey))
            throw new AiUnavailableException("Anthropic API key is not configured (Ai:Anthropic:ApiKey).");

        var content = new JsonArray();
        if (image != null)
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MimeType, ["data"] = image.Base64 }
            });
        content.Add(new JsonObject { ["type"] = "text", ["text"] = userPrompt });

        var body = new JsonObject
        {
            ["model"] = image != null ? VisionModel : Model,
            ["max_tokens"] = _options.MaxOutputTokens,
            ["temperature"] = _options.Temperature,
            ["system"] = systemPrompt,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content })
        };

        using var request = Request(HttpMethod.Post, "/v1/messages");
        request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new AiUnavailableException("Cannot reach the Anthropic API.", ex); }

        using (response)
        {
            var text = await AiHttp.ReadOrThrowAsync(response, Name, "Anthropic", ct);
            var parts = JsonNode.Parse(text)?["content"]?.AsArray()
                .Where(c => c?["type"]?.GetValue<string>() == "text")
                .Select(c => c?["text"]?.GetValue<string>())
                .Where(t => t != null)
                .ToList();
            return parts is { Count: > 0 }
                ? string.Join("", parts)
                : throw new AiOutputException("Anthropic returned an empty response.");
        }
    }

    public async Task<AiHealth> CheckAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.Anthropic.ApiKey))
            return new AiHealth(Name, Model, false, false, true, "Anthropic API key is not configured (Ai:Anthropic:ApiKey).");
        try
        {
            using var request = Request(HttpMethod.Get, "/v1/models");
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? new AiHealth(Name, Model, true, true, true, "Anthropic API reachable.")
                : new AiHealth(Name, Model, true, false, true, $"Anthropic API returned HTTP {(int)response.StatusCode}. Check the API key.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AiHealth(Name, Model, true, false, true, "Cannot reach the Anthropic API from the server.");
        }
    }
}

/// <summary>
/// OpenAI-compatible Chat Completions API. Used for Groq (free tier) and for
/// OpenAI or any compatible gateway. Credentials stay on the backend.
/// </summary>
public sealed class OpenAiCompatibleProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly AiOptions.OpenAiOptions _endpoint;
    private readonly string _configKey;

    public OpenAiCompatibleProvider(HttpClient http, AiOptions options, string name, AiOptions.OpenAiOptions endpoint, string configKey)
    {
        _http = http;
        _options = options;
        _endpoint = endpoint;
        _configKey = configKey;
        Name = name;
    }

    public string Name { get; }
    public string Model => _endpoint.Model;
    public string VisionModel => string.IsNullOrWhiteSpace(_endpoint.VisionModel) ? Model : _endpoint.VisionModel;
    public bool IsExternal => !_endpoint.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _endpoint.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(_endpoint.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.ApiKey);
        return request;
    }

    private string MissingKey => $"{Name} API key is not configured (Ai:{_configKey}:ApiKey). Set it with dotnet user-secrets.";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct, AiImage? image = null)
    {
        if (IsExternal && string.IsNullOrWhiteSpace(_endpoint.ApiKey))
            throw new AiUnavailableException(MissingKey);

        JsonNode userContent = image == null
            ? JsonValue.Create(userPrompt)!
            : new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = userPrompt },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = image.DataUrl } });

        var model = image != null ? VisionModel : Model;
        bool reasoningModel = model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase);
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = _options.Temperature,
            // Reasoning models spend tokens thinking first: keep effort low and leave room for the answer.
            ["max_tokens"] = reasoningModel ? Math.Max(_options.MaxOutputTokens, 3000) : _options.MaxOutputTokens,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userContent })
        };
        if (reasoningModel) body["reasoning_effort"] = "low";
        // Qwen 3.x on Groq: no thinking needed for reading a document; keeps it fast.
        if (model.Contains("qwen3", StringComparison.OrdinalIgnoreCase)) body["reasoning_effort"] = "none";

        using var request = Request(HttpMethod.Post, "/chat/completions");
        request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new AiUnavailableException($"Cannot reach the {Name} API.", ex); }

        using (response)
        {
            var text = await AiHttp.ReadOrThrowAsync(response, Name, _configKey, ct);
            return JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                   ?? throw new AiOutputException("The model returned an empty response.");
        }
    }

    public async Task<AiHealth> CheckAsync(CancellationToken ct)
    {
        if (IsExternal && string.IsNullOrWhiteSpace(_endpoint.ApiKey))
            return new AiHealth(Name, Model, false, false, IsExternal, MissingKey);
        try
        {
            using var request = Request(HttpMethod.Get, "/models");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new AiHealth(Name, Model, true, false, IsExternal, $"{Name} returned HTTP {(int)response.StatusCode}. Check the API key.");

            // Confirm the configured model is actually available to this key.
            var ids = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["data"]?.AsArray()
                .Select(m => m?["id"]?.GetValue<string>() ?? "").Where(id => id.Length > 0).ToList() ?? new List<string>();
            var usable = ids.Where(i => !i.Contains("whisper") && !i.Contains("tts") && !i.Contains("guard")).OrderBy(i => i).ToList();
            bool? vision = ids.Count == 0 ? null : ids.Contains(VisionModel, StringComparer.OrdinalIgnoreCase);
            if (ids.Count > 0 && !ids.Contains(Model, StringComparer.OrdinalIgnoreCase))
                return new AiHealth(Name, Model, true, false, IsExternal,
                    $"Model '{Model}' is not available to this API key. Set Ai:{_configKey}:Model to one of: {string.Join(", ", usable.Take(10))}.",
                    VisionModel, vision, usable);
            return new AiHealth(Name, Model, true, true, IsExternal, $"{Name} reachable ({Model}). Client identifiers are redacted before sending.",
                VisionModel, vision, usable);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AiHealth(Name, Model, true, false, IsExternal, $"Cannot reach {Name} from the server.");
        }
    }
}

public static class AiProviderFactory
{
    public const string HttpClientName = "ai";

    public static IAiProvider Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<AiOptions>>().Value;
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);

        return options.Provider?.Trim().ToLowerInvariant() switch
        {
            "groq" => new OpenAiCompatibleProvider(http, options, "Groq", options.Groq, "Groq"),
            "openai" => new OpenAiCompatibleProvider(http, options, "OpenAI", options.OpenAI, "OpenAI"),
            "ollama" => new OllamaProvider(http, options),
            "anthropic" => new AnthropicProvider(http, options),
            _ => new DisabledAiProvider()
        };
    }
}

/// <summary>Extracts and parses the JSON object from a model reply.</summary>
public static class AiJson
{
    public static JsonElement ParseObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new AiOutputException("The AI returned an empty response.");

        var text = raw.Trim();
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new AiOutputException("The AI response was not valid JSON.");

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new AiOutputException("The AI response was not a JSON object.");
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AiOutputException("The AI response was not valid JSON.");
        }
    }

    public static string RequiredString(JsonElement obj, string name, int minLength, int maxLength)
    {
        var value = OptionalString(obj, name, maxLength);
        if (value == null || value.Length < minLength)
            throw new AiOutputException($"The AI response is missing a valid '{name}'.");
        return value;
    }

    public static string? OptionalString(JsonElement obj, string name, int maxLength)
    {
        if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return null;
        var s = p.GetString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length > maxLength ? s[..maxLength].TrimEnd() + "…" : s;
    }

    public static List<string> StringList(JsonElement obj, string name, int maxItems, int maxItemLength)
    {
        if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) return new List<string>();
        return p.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()?.Trim() ?? "")
            .Where(s => s.Length > 0)
            .Select(s => s.Length > maxItemLength ? s[..maxItemLength].TrimEnd() + "…" : s)
            .Distinct()
            .Take(maxItems)
            .ToList();
    }

    public static bool? OptionalBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
            ? p.GetBoolean()
            : null;
}
