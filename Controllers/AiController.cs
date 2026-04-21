using Microsoft.AspNetCore.Mvc;
using WhipAI.Helpers;
using WhipAI.Models;
using WhipAI.Skills;

namespace WhipAI.Controllers;

/// <summary>
/// Generic skill invocation endpoint. Every skill (today just
/// <c>argyle-driver-review</c>) is called through this one route,
/// dispatched by name. WhipBridge hits
/// <c>POST /api/ai/skill/{name}/invoke</c> with an <c>x-api-key</c>
/// header — browsers never call WhipAI directly.
/// </summary>
[ApiController]
[Route("api/ai")]
[ApiKeyAuth]
public sealed class AiController : ControllerBase
{
    private readonly SkillRegistry _registry;
    private readonly ILogger<AiController> _logger;

    public AiController(SkillRegistry registry, ILogger<AiController> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Invokes a registered skill. Returns 404 if the skill doesn't exist,
    /// 400 on missing input, 200 on success / handled-failure (see
    /// <see cref="SkillResponse.Ok"/>). Timeouts or Anthropic outages come
    /// back as 503 when <c>AI_FAIL_OPEN=true</c> so the frontend can fall
    /// back to its local renderer gracefully.
    /// </summary>
    [HttpPost("skill/{name}/invoke")]
    public async Task<IActionResult> Invoke(string name, [FromBody] SkillRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new { ok = false, error = "Request body is required." });
        }

        if (!_registry.TryGet(name, out var skill))
        {
            return NotFound(new { ok = false, error = $"Skill '{name}' is not registered." });
        }

        // audit_user comes from WhipBridge as a field on the request body
        // (see SkillRequest.AuditUser). We don't infer it from auth — the
        // transport is server-to-server, so there's no user identity on
        // the JWT to lean on.
        var auditUser = request.AuditUser ?? "unknown";
        var tokenEnvironment = Request.Headers["token_environment"].ToString();

        try
        {
            var response = await skill.InvokeAsync(request, auditUser, tokenEnvironment, ct);
            return Ok(response);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status499ClientClosedRequest,
                new { ok = false, error = "Request was cancelled by the client." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in skill {Skill}", name);
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
    }

}
