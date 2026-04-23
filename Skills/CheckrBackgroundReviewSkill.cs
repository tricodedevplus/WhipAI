using System.Text.Json;
using WhipAI.Helpers;
using WhipAI.Models;
using WhipAI.Services;

namespace WhipAI.Skills;

/// <summary>
/// Analyzes an applicant's Checkr criminal background-check JSON and
/// returns a compact HTML report with an Approve / Review Manually / Deny
/// recommendation. The system prompt (production-authored) lives in
/// <c>Skills/Prompts/checkr-background-review.skill</c> — an Anthropic
/// skill bundle (ZIP containing <c>checkr-background-review/SKILL.md</c>).
///
/// Input: the raw Checkr payload stored per-applicant in
/// <c>fleet_migration.crm_applicant_checkr_data</c>. Shape is whatever
/// Checkr returns (results[], results_info, person, etc.) — the skill's
/// prompt handles extraction + gracefully degrades on missing fields.
///
/// Output: a single self-contained HTML block (per the skill's contract).
/// Same best-effort JSON/text wrap as ArgyleDriverReviewSkill so the
/// caller shape stays consistent.
/// </summary>
public sealed class CheckrBackgroundReviewSkill : BaseSkill
{
    public override string Name => "checkr-background-review";
    public override string Version => "v1";
    public override string Description =>
        "Reviews an applicant's Checkr background-check payload and " +
        "produces a compact HTML report with Approve / Review Manually / " +
        "Deny recommendation, deduplicating records and weighting by " +
        "recency, severity, driving relevance, and pattern.";

    public CheckrBackgroundReviewSkill(
        AnthropicService anthropic,
        ILogger<CheckrBackgroundReviewSkill> log)
        : base(anthropic, log) { }

    protected override async Task<SkillResponse> RunAsync(
        SkillRequest request,
        string auditUser,
        CancellationToken ct)
    {
        // Checkr-specific redaction: keeps names / DOB / address because
        // the skill's identity-match rules require them. Only scrubs true
        // financial identifiers (SSN, bank, card). See PiiRedactor.
        var redacted = PiiRedactor.RedactCheckrInput(request.Input);

        // The skill expects the raw Checkr payload — the prompt author
        // already knows its shape, so we don't add framing.
        var userContent = JsonSerializer.Serialize(redacted, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        var result = await Anthropic.InvokeAsync(
            systemPrompt: SystemPrompt,
            userContent: userContent,
            options: new AnthropicInvocationOptions
            {
                // Checkr HTML reports are similar size to the Argyle ones
                // (~5K-10K tokens of inline-styled HTML). 16384 leaves
                // comfortable headroom; adaptive thinking keeps short
                // payloads cheap.
                MaxTokens = 16384,
                Effort = "high",
                AdaptiveThinking = true,
            },
            ct: ct);

        if (!result.Ok)
        {
            return SkillResponse.Failure(result.Error ?? "Unknown Anthropic error.", result.Meta);
        }

        // Strip markdown code fences if the model ever wraps the HTML — the
        // skill's contract is "HTML only, no backticks" but models
        // occasionally drift, so we normalize defensively here.
        var rawText = result.Text.Trim();
        if (rawText.StartsWith("```"))
        {
            var firstNewline = rawText.IndexOf('\n');
            if (firstNewline >= 0) rawText = rawText[(firstNewline + 1)..];
            if (rawText.EndsWith("```")) rawText = rawText[..^3];
            rawText = rawText.Trim();
        }

        // Same "best-effort JSON, fall back to { text }" shape as Argyle so
        // the frontend treats both skills identically.
        object? data;
        try
        {
            data = JsonSerializer.Deserialize<JsonElement>(rawText);
        }
        catch
        {
            data = new { text = rawText };
        }

        return SkillResponse.Success(data!, result.Meta!);
    }
}
