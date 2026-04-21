using WhipAI.Models;

namespace WhipAI.Skills;

/// <summary>
/// A Skill is one self-contained AI capability — a named recipe with a
/// stable system prompt, input/output contract, and cost/latency profile.
/// Each skill has ONE purpose ("render Argyle info", "classify applicant
/// note", etc.). Skills are versioned so prompts can evolve without
/// breaking callers.
///
/// To add a new skill: subclass <see cref="BaseSkill"/>, place its system
/// prompt at <c>Skills/Prompts/{name}.{version}.md</c>, and register it in
/// <c>Program.cs</c>.
/// </summary>
public interface ISkill
{
    /// <summary>Stable identifier used in the URL: <c>/api/ai/skill/{Name}/invoke</c>.</summary>
    string Name { get; }

    /// <summary>Version string (e.g. <c>"v1"</c>). Bumped when the prompt changes meaningfully.</summary>
    string Version { get; }

    /// <summary>One-line description for the catalog / admin UI.</summary>
    string Description { get; }

    /// <summary>
    /// Runs the skill. Receives the raw <see cref="SkillRequest"/> from the
    /// controller, returns the user-facing response envelope. Implementations
    /// are responsible for input parsing, user-content formatting, calling
    /// <see cref="Services.AnthropicService"/>, and output shaping.
    /// </summary>
    Task<SkillResponse> InvokeAsync(
        SkillRequest request,
        string auditUser,
        string? tokenEnvironment,
        CancellationToken ct);
}
