using System.Text.Json;

namespace WhipAI.Models;

/// <summary>
/// Body sent by the frontend to <c>POST /api/ai/skill/{name}/invoke</c>.
/// The <see cref="Input"/> shape is skill-specific — each skill defines what
/// it expects and parses the JsonElement itself.
/// </summary>
public sealed class SkillRequest
{
    /// <summary>
    /// Arbitrary JSON payload passed to the skill. Shape is whatever the
    /// skill documents in its class — for <c>render-argyle</c> this looks
    /// like <c>{ jsonInfo, jsonRegister, driverRecord? }</c>.
    /// </summary>
    public JsonElement Input { get; set; }

    /// <summary>
    /// Optional per-request overrides. Null means "use the skill's defaults".
    /// </summary>
    public SkillRequestOptions? Options { get; set; }

    /// <summary>
    /// Who is making this call, as forwarded by WhipBridge from the browser's
    /// JWT. Used only for <c>ai_invocations</c> logging so the admin dashboard
    /// can attribute costs. WhipAI does NOT validate this — trust comes from
    /// the x-api-key header, not the claimed user.
    /// </summary>
    public string? AuditUser { get; set; }
}

public sealed class SkillRequestOptions
{
    /// <summary>Override the skill's default model (e.g. downgrade to Haiku for speed).</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Pin to a specific version of the skill's prompt. Null = latest.
    /// Useful for A/B testing or rollbacks without redeploying.
    /// </summary>
    public string? Version { get; set; }
}
