using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace WhipAI.Helpers;

/// <summary>
/// Gates a controller / action behind a shared-secret <c>x-api-key</c>
/// header matched against the <c>WHIPAI_API_KEY</c> env var. Same pattern
/// WhipBridge uses for its internal-only endpoints
/// (<c>INTERNAL_API_TOKEN</c>, <c>SIMPLETALK_API_TOKEN</c>).
///
/// Chosen over JWT bearer because the only caller is WhipBridge, not a
/// browser. A shared key:
/// - Never touches the frontend (can't be stolen via XSS)
/// - Rotates independently of user sessions
/// - Makes the trust boundary explicit: "this call came from our backend,
///   full stop"
///
/// The actual user performing the action is conveyed by WhipBridge as a
/// field in the request body (e.g. <c>audit_user</c>), not as an auth
/// primitive on the transport.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ApiKeyAuthAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var expected = Environment.GetEnvironmentVariable("WHIPAI_API_KEY");
        if (string.IsNullOrWhiteSpace(expected))
        {
            // Fail closed — a misconfigured server with no key should reject
            // everything rather than silently accept anything.
            context.Result = new ObjectResult(new
            {
                ok = false,
                error = "WHIPAI_API_KEY is not configured on this server.",
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        var provided = context.HttpContext.Request.Headers["x-api-key"].ToString();
        if (string.IsNullOrWhiteSpace(provided) || !ConstantTimeEquals(provided, expected))
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                ok = false,
                error = "Invalid or missing x-api-key.",
            });
            return;
        }

        await next();
    }

    /// <summary>
    /// Constant-time string comparison to resist timing attacks against
    /// the shared secret. Stdlib <c>string.Equals</c> can short-circuit on
    /// the first mismatched character, which leaks key bytes over many
    /// probes.
    /// </summary>
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
