using Microsoft.AspNetCore.Mvc;
using WhipAI.Helpers;
using WhipAI.Skills;

namespace WhipAI.Controllers;

/// <summary>
/// Catalog endpoint — lists every registered skill with its version and
/// description. Admin UI / developer tools use this to render a picker
/// without hard-coding skill names.
/// </summary>
[ApiController]
[Route("api/ai/skills")]
[ApiKeyAuth]
public sealed class SkillsController : ControllerBase
{
    private readonly SkillRegistry _registry;

    public SkillsController(SkillRegistry registry)
    {
        _registry = registry;
    }

    [HttpGet]
    public IActionResult List()
    {
        var skills = _registry.All()
            .Select(s => new { name = s.Name, version = s.Version, description = s.Description })
            .OrderBy(s => s.name)
            .ToList();

        return Ok(new { ok = true, data = skills });
    }
}
