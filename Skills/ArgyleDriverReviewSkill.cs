using System.Text.Json;
using WhipAI.Helpers;
using WhipAI.Models;
using WhipAI.Services;

namespace WhipAI.Skills;

/// <summary>
/// Analyzes an applicant's Argyle payload and returns a driver-review
/// summary. The system prompt (production-authored) lives in
/// <c>Skills/Prompts/argyle-driver-review.skill</c> — an Anthropic skill
/// bundle (ZIP containing <c>argyle-driver-review/SKILL.md</c>).
///
/// Input: the raw Argyle payload the frontend has on hand for this
/// applicant. Shape is intentionally loose — whatever WhipFlow has
/// (identity JSON, registration JSON, driver record, etc.) is forwarded.
/// The skill's system prompt is responsible for extracting what it needs
/// and gracefully degrading when fields are missing.
///
/// Output: whatever the skill's prompt specifies. We pass the raw text
/// through with a best-effort JSON parse — if it's valid JSON we return
/// the object; otherwise we return <c>{ text: "..." }</c> so callers
/// always get a usable shape.
/// </summary>
public sealed class ArgyleDriverReviewSkill : BaseSkill
{
    public override string Name => "argyle-driver-review";
    public override string Version => "v1";
    public override string Description =>
        "Reviews an applicant's Argyle payload and produces a structured " +
        "summary (identity, employment tenure, earnings, ratings, risk flags) " +
        "for use in WhipFlow's applicant panel.";

    public ArgyleDriverReviewSkill(
        AnthropicService anthropic,
        InvocationLogger logger,
        ILogger<ArgyleDriverReviewSkill> log)
        : base(anthropic, logger, log) { }

    protected override async Task<SkillResponse> RunAsync(
        SkillRequest request,
        string auditUser,
        CancellationToken ct)
    {
        // Redact PII we don't need (SSN, DOB, bank numbers). The driver
        // review is about employment + earnings + ratings; identity fields
        // the skill actually uses (first_name, last_name, email) are kept.
        var redacted = PiiRedactor.RedactArgyleInput(request.Input);

        // User-content message is just the Argyle payload stringified. The
        // system prompt (loaded from the .skill bundle) defines what to do
        // with it — we don't add framing here because the prompt author
        // already expects the raw payload format.
        var userContent = JsonSerializer.Serialize(redacted, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        var result = await Anthropic.InvokeAsync(
            systemPrompt: SystemPrompt,
            userContent: userContent,
            options: new AnthropicInvocationOptions
            {
                // The skill emits a rich HTML report (~5K-10K tokens with
                // inline CSS). First run truncated at 4096, so give it
                // 16384 — well under Opus 4.7's 128K cap. Adaptive thinking
                // means short payloads still cost almost nothing.
                MaxTokens = 16384,
                Effort = "high",
                AdaptiveThinking = true,
            },
            ct: ct);

        if (!result.Ok)
        {
            return SkillResponse.Failure(result.Error ?? "Unknown Anthropic error.", result.Meta);
        }

        // Best-effort JSON parse so callers get structured data when the
        // prompt emits JSON, and an envelope with the raw text otherwise.
        var rawText = result.Text.Trim();
        if (rawText.StartsWith("```"))
        {
            var firstNewline = rawText.IndexOf('\n');
            if (firstNewline >= 0) rawText = rawText[(firstNewline + 1)..];
            if (rawText.EndsWith("```")) rawText = rawText[..^3];
            rawText = rawText.Trim();
        }

        object? data;
        try
        {
            data = JsonSerializer.Deserialize<JsonElement>(rawText);
        }
        catch
        {
            // Not JSON — wrap raw text so the frontend always sees a
            // predictable shape. If the skill's contract evolves to JSON
            // later, clients that already handle { text } still work.
            data = new { text = rawText };
        }

        return SkillResponse.Success(data!, result.Meta!);
    }
}
