using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhipAI.Models;

namespace WhipAI.Services;

/// <summary>
/// Thin wrapper over Anthropic's REST API (<c>POST /v1/messages</c>). Skills
/// call <see cref="InvokeAsync"/> with a system prompt and user content;
/// this class handles the wire format (prompt caching via
/// <c>cache_control</c>, effort, adaptive thinking), parses the response,
/// and computes invocation cost + token usage.
///
/// Why raw HTTP instead of an SDK:
/// - The only NuGet package named <c>Anthropic</c> is a community-maintained
///   SDK (tryAGI). Anthropic's official C# SDK isn't published on NuGet.
/// - The Messages API has one real endpoint; a hand-rolled client is smaller
///   than pulling in a 3rd-party dep, and it's the same pattern WhipBridge
///   uses for every other integration.
/// - Gives deterministic control over prompt caching — we decide exactly
///   which blocks carry <c>cache_control</c> without fighting an abstraction.
///
/// One instance per app lifetime (singleton) — HttpClient is designed for
/// reuse; creating one per request exhausts ephemeral ports under load.
/// </summary>
public sealed class AnthropicService
{
    private const string ApiBaseUrl = "https://api.anthropic.com";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly ILogger<AnthropicService> _logger;
    private readonly string _apiKey;
    private readonly string _defaultModel;
    private readonly bool _failOpen;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AnthropicService(ILogger<AnthropicService> logger)
    {
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        _defaultModel = Environment.GetEnvironmentVariable("DEFAULT_MODEL") ?? "claude-opus-4-7";
        _failOpen = (Environment.GetEnvironmentVariable("AI_FAIL_OPEN") ?? "true")
            .Equals("true", StringComparison.OrdinalIgnoreCase);

        _http = new HttpClient
        {
            BaseAddress = new Uri(ApiBaseUrl),
            // 120s is generous but needed for Opus with adaptive thinking;
            // the SDK rule-of-thumb is "use streaming for >16K max_tokens" —
            // our skills cap at 4K so a sync POST is fine.
            Timeout = TimeSpan.FromSeconds(120),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
            _http.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
            _logger.LogInformation("AnthropicService ready (default model: {Model}, fail_open: {FailOpen})",
                _defaultModel, _failOpen);
        }
        else
        {
            _logger.LogWarning("ANTHROPIC_API_KEY is not set — skill invocations will 503 until it is configured.");
        }
    }

    public bool IsReady => !string.IsNullOrWhiteSpace(_apiKey);

    /// <summary>
    /// Invokes Claude with a cached system prompt and a plain-text user
    /// message, returns the first text block from the response along with
    /// token usage. The system prompt is marked
    /// <c>cache_control: ephemeral</c> so repeated invocations hit the
    /// cache (~90% off on input tokens).
    /// </summary>
    public async Task<AnthropicInvocationResult> InvokeAsync(
        string systemPrompt,
        string userContent,
        AnthropicInvocationOptions options,
        CancellationToken ct = default)
    {
        if (!IsReady)
        {
            return AnthropicInvocationResult.Failure(
                "ANTHROPIC_API_KEY is not configured on this server.",
                serviceUnavailable: true);
        }

        var stopwatch = Stopwatch.StartNew();
        var model = string.IsNullOrWhiteSpace(options.Model) ? _defaultModel : options.Model!;

        // Messages API wire format — see
        // https://docs.claude.com/en/build-with-claude/prompt-caching for
        // the cache_control placement contract.
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = options.MaxTokens,
            ["system"] = new[]
            {
                new
                {
                    type = "text",
                    text = systemPrompt,
                    cache_control = new { type = "ephemeral" },
                },
            },
            ["messages"] = new[]
            {
                new { role = "user", content = userContent },
            },
            ["output_config"] = new
            {
                effort = options.Effort,
            },
        };

        if (options.AdaptiveThinking)
        {
            // Opus 4.7 rejects the older fixed-budget mode — adaptive is the
            // only supported on-switch. Claude decides depth per request.
            payload["thinking"] = new { type = "adaptive" };
        }

        var body = JsonSerializer.Serialize(payload, JsonOpts);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            using var response = await _http.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic returned {Status}: {Body}",
                    (int)response.StatusCode,
                    responseBody.Length > 500 ? responseBody[..500] + "..." : responseBody);

                // 5xx / 529 / 429 → treat as service-unavailable so the
                // frontend can fall back. 4xx (bad request, 401, 403) is
                // surfaced so the dev knows to fix something on our side.
                var statusCode = (int)response.StatusCode;
                var serviceUnavailable = _failOpen && statusCode >= 500;
                return AnthropicInvocationResult.Failure(
                    TryExtractError(responseBody) ?? $"Anthropic HTTP {statusCode}",
                    serviceUnavailable);
            }

            // Parse the response: text from the first content block + usage.
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var text = ExtractFirstText(root);
            var (inTok, outTok, cacheRead, cacheCreate) = ExtractUsage(root);

            var meta = new SkillInvocationMeta
            {
                Model = model,
                InputTokens = inTok,
                OutputTokens = outTok,
                CacheReadTokens = cacheRead,
                CacheCreationTokens = cacheCreate,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                CostUsd = EstimateCostUsd(model, inTok, outTok, cacheRead, cacheCreate),
            };

            _logger.LogInformation(
                "Anthropic call ok — model={Model}, in={In}, out={Out}, cache_read={CacheRead}, " +
                "cost=${Cost:F5}, latency={Latency}ms",
                model, meta.InputTokens, meta.OutputTokens, meta.CacheReadTokens, meta.CostUsd, meta.LatencyMs);

            return AnthropicInvocationResult.Success(text, meta);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return AnthropicInvocationResult.Failure("Request was cancelled by the client.", serviceUnavailable: false);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Anthropic call failed for model={Model}", model);
            return AnthropicInvocationResult.Failure(ex.Message, serviceUnavailable: _failOpen);
        }
    }

    private static string ExtractFirstText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type)
                && type.GetString() == "text"
                && block.TryGetProperty("text", out var text))
            {
                return text.GetString() ?? "";
            }
        }
        return "";
    }

    private static (int input, int output, int cacheRead, int cacheCreate) ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
            return (0, 0, 0, 0);

        static int Get(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        return (
            Get(usage, "input_tokens"),
            Get(usage, "output_tokens"),
            Get(usage, "cache_read_input_tokens"),
            Get(usage, "cache_creation_input_tokens")
        );
    }

    private static string? TryExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg))
            {
                return msg.GetString();
            }
        }
        catch { /* swallow — just fall through to generic error */ }
        return null;
    }

    /// <summary>
    /// Rough cost estimate in USD for a single invocation. Prices are cached
    /// from the Claude API skill doc (2026-04-15). Cache reads are ~0.1×
    /// input price; cache writes (5-min TTL) are ~1.25× input price.
    /// Use for internal tracking — not for billing.
    /// </summary>
    private static decimal EstimateCostUsd(
        string model,
        int inputTokens,
        int outputTokens,
        int cacheReadTokens,
        int cacheCreationTokens)
    {
        var (inPerMil, outPerMil) = model switch
        {
            "claude-opus-4-7" or "claude-opus-4-6" => (5.00m, 25.00m),
            "claude-sonnet-4-6" => (3.00m, 15.00m),
            "claude-haiku-4-5" or "claude-haiku-4-5-20251001" => (1.00m, 5.00m),
            _ => (5.00m, 25.00m),
        };

        return (inputTokens / 1_000_000m) * inPerMil
            + (outputTokens / 1_000_000m) * outPerMil
            + (cacheReadTokens / 1_000_000m) * inPerMil * 0.1m
            + (cacheCreationTokens / 1_000_000m) * inPerMil * 1.25m;
    }
}

public sealed class AnthropicInvocationOptions
{
    public string? Model { get; set; }
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// One of <c>"low"</c>, <c>"medium"</c>, <c>"high"</c>, <c>"xhigh"</c>,
    /// <c>"max"</c>. See the Claude API skill docs for per-model support.
    /// Default <c>"high"</c> matches the skill's recommendation for
    /// intelligence-sensitive work.
    /// </summary>
    public string Effort { get; set; } = "high";

    /// <summary>
    /// Enable adaptive thinking. Default true — Claude decides depth per
    /// request, and simple tasks cost almost nothing extra. Disable only
    /// if the skill is latency-critical.
    /// </summary>
    public bool AdaptiveThinking { get; set; } = true;
}

public sealed class AnthropicInvocationResult
{
    public bool Ok { get; set; }
    public string Text { get; set; } = "";
    public SkillInvocationMeta? Meta { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// True when the failure is one the frontend should treat as "Claude
    /// temporarily down" (retry or fall back to local renderer). False for
    /// invalid-request / bad-input style errors the frontend should surface.
    /// </summary>
    public bool ServiceUnavailable { get; set; }

    public static AnthropicInvocationResult Success(string text, SkillInvocationMeta meta) =>
        new() { Ok = true, Text = text, Meta = meta };

    public static AnthropicInvocationResult Failure(string error, bool serviceUnavailable = false) =>
        new() { Ok = false, Error = error, ServiceUnavailable = serviceUnavailable };
}
