using WhipAI.Models;
using WhipAI.Services;

namespace WhipAI.Skills;

/// <summary>
/// Shared plumbing for every skill: loads the system prompt from disk at
/// construction time and exposes helpers for subclasses to call Anthropic
/// cleanly. WhipAI is stateless — any persistence of the response
/// (caching, audit) happens on the caller side (WhipBridge) via SPs.
///
/// Subclasses override <see cref="RunAsync"/> with their own
/// input-parsing + output-shaping logic. They DON'T need to touch prompt
/// loading — <see cref="InvokeAsync"/> handles it.
/// </summary>
public abstract class BaseSkill : ISkill
{
    public abstract string Name { get; }
    public abstract string Version { get; }
    public abstract string Description { get; }

    protected readonly AnthropicService Anthropic;
    protected readonly ILogger BaseLogger;

    /// <summary>
    /// System prompt content loaded from <c>Skills/Prompts/{Name}.{Version}.md</c>.
    /// Loaded once per process; update + redeploy (or hot-reload file and
    /// restart) to iterate. Kept as a property so subclasses can inspect it.
    /// </summary>
    protected string SystemPrompt { get; }

    protected BaseSkill(AnthropicService anthropic, ILogger baseLogger)
    {
        Anthropic = anthropic;
        BaseLogger = baseLogger;
        SystemPrompt = LoadPromptFromDisk();
    }

    private string LoadPromptFromDisk()
    {
        // Two supported formats:
        //   1. Anthropic .skill bundle — a ZIP archive containing
        //      {Name}/SKILL.md inside (this is the standard Anthropic
        //      skill package format). Looked up as {Name}.skill.
        //   2. Plain .md fallback — {Name}.{Version}.md. Used when a skill
        //      hasn't been packaged yet or was authored inline.
        //
        // Both are searched in the compiled output dir first, then the
        // source tree (for `dotnet run` from the repo root in dev).
        var searchRoots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Skills", "Prompts"),
            Path.Combine(Directory.GetCurrentDirectory(), "Skills", "Prompts"),
        };

        foreach (var root in searchRoots)
        {
            var skillFile = Path.Combine(root, $"{Name}.skill");
            if (File.Exists(skillFile))
            {
                var extracted = TryExtractFromSkillBundle(skillFile);
                if (!string.IsNullOrWhiteSpace(extracted)) return extracted;
            }

            var mdFile = Path.Combine(root, $"{Name}.{Version}.md");
            if (File.Exists(mdFile))
            {
                try { return File.ReadAllText(mdFile); }
                catch { /* fall through */ }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Opens a <c>.skill</c> ZIP bundle and reads <c>{Name}/SKILL.md</c>
    /// from it. Returns empty string if the archive is malformed or the
    /// expected entry is missing — the caller handles fallback.
    /// </summary>
    private string TryExtractFromSkillBundle(string bundlePath)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(bundlePath);
            // Anthropic's skill format places SKILL.md inside a folder named
            // after the skill. We accept the canonical path first, then fall
            // back to any top-level SKILL.md as a last resort.
            var preferredPath = $"{Name}/SKILL.md";
            var entry = archive.Entries.FirstOrDefault(e =>
                            string.Equals(e.FullName, preferredPath, StringComparison.OrdinalIgnoreCase))
                        ?? archive.Entries.FirstOrDefault(e =>
                            e.FullName.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase)
                            || e.FullName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));

            if (entry is null) return string.Empty;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Controller-facing entrypoint. Handles preconditions (prompt loaded),
    /// delegates to <see cref="RunAsync"/> for the skill-specific logic,
    /// and stamps the skill identity onto the response meta. WhipAI is
    /// stateless — callers (WhipBridge) own persistence.
    /// </summary>
    public async Task<SkillResponse> InvokeAsync(
        SkillRequest request,
        string auditUser,
        string? tokenEnvironment,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SystemPrompt))
        {
            var err = $"Skill '{Name}' has no system prompt on disk at Skills/Prompts/{Name}.{Version}.md.";
            BaseLogger.LogError(err);
            return SkillResponse.Failure(err);
        }

        try
        {
            var result = await RunAsync(request, auditUser, ct);
            // Stamp the skill identity onto the meta so callers see
            // "skill: argyle-driver-review, version: v1" in every response.
            // AnthropicService doesn't know about skills — only this layer
            // does — so this is the natural place to fill them in.
            if (result.Meta is not null)
            {
                result.Meta.Skill = Name;
                result.Meta.Version = Version;
            }
            return result;
        }
        catch (Exception ex)
        {
            BaseLogger.LogError(ex, "Skill '{Skill}' threw unhandled exception", Name);
            return SkillResponse.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Skill-specific implementation — called from <see cref="InvokeAsync"/>
    /// after the system prompt is confirmed loaded. Subclasses build the
    /// user-content string, call <see cref="AnthropicService.InvokeAsync"/>,
    /// and shape the <see cref="SkillResponse"/>.
    /// </summary>
    protected abstract Task<SkillResponse> RunAsync(
        SkillRequest request,
        string auditUser,
        CancellationToken ct);
}
