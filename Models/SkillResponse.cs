namespace WhipAI.Models;

/// <summary>
/// Standard envelope returned by every skill invocation. Matches the
/// <c>{ ok, data, error }</c> shape the DriveWhip frontend already uses.
/// </summary>
public sealed class SkillResponse
{
    public bool Ok { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Per-invocation metadata: skill name + version, model used, token
    /// counts, cost, and latency. Frontend can ignore it — it exists so
    /// the admin dashboard can show costs per feature.
    /// </summary>
    public SkillInvocationMeta? Meta { get; set; }

    public static SkillResponse Success(object data, SkillInvocationMeta meta) =>
        new() { Ok = true, Data = data, Meta = meta };

    public static SkillResponse Failure(string error, SkillInvocationMeta? meta = null) =>
        new() { Ok = false, Error = error, Meta = meta };
}

public sealed class SkillInvocationMeta
{
    public string Skill { get; set; } = "";
    public string Version { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreationTokens { get; set; }
    public decimal CostUsd { get; set; }
    public long LatencyMs { get; set; }
}
