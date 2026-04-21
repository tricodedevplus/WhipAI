using MySql.Data.MySqlClient;
using WhipAI.Models;

namespace WhipAI.Services;

/// <summary>
/// Persists every skill invocation into <c>ai_invocations</c> so the admin
/// dashboard can chart cost per feature and the oncall can triage anomalies
/// ("why did WhipAI spend $300 last night"). All writes are fire-and-forget —
/// a DB hiccup never blocks the user's response.
/// </summary>
public sealed class InvocationLogger
{
    private readonly MySqlService _mysql;
    private readonly ILogger<InvocationLogger> _logger;
    private readonly bool _enabled;

    public InvocationLogger(MySqlService mysql, ILogger<InvocationLogger> logger)
    {
        _mysql = mysql;
        _logger = logger;

        // Enable only when at least one MySQL connection string is set.
        // Lets you run WhipAI in isolation (no DB) for skill-prompt
        // iteration without polluting logs with "failed to log" warnings
        // every call. Flip to enabled the moment CNDW or CNDW_QA appears.
        var hasDb = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CNDW"))
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CNDW_QA"));
        _enabled = hasDb;

        if (!_enabled)
        {
            _logger.LogInformation("InvocationLogger disabled — no CNDW/CNDW_QA env var set. " +
                "Skill invocations will NOT be persisted to ai_invocations.");
        }
    }

    /// <summary>
    /// Write one row to <c>ai_invocations</c>. Never throws — a failure here
    /// is logged as a warning and swallowed. See <c>sql/ai_invocations.sql</c>
    /// for the table definition.
    /// </summary>
    public void Log(
        string skill,
        string version,
        string auditUser,
        SkillInvocationMeta? meta,
        bool ok,
        string? error,
        string? tokenEnvironment)
    {
        if (!_enabled) return;

        _ = Task.Run(() =>
        {
            try
            {
                using var conn = _mysql.OpenConnection(tokenEnvironment);
                using var cmd = new MySqlCommand(@"
                    INSERT INTO ai_invocations
                        (skill, version, model, audit_user,
                         input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                         cost_usd, latency_ms, ok, error_message, created_at)
                    VALUES
                        (@skill, @version, @model, @audit_user,
                         @in_tok, @out_tok, @cache_read, @cache_creation,
                         @cost, @latency, @ok, @error, UTC_TIMESTAMP())",
                    conn);

                cmd.Parameters.AddWithValue("@skill", skill);
                cmd.Parameters.AddWithValue("@version", version);
                cmd.Parameters.AddWithValue("@model", meta?.Model ?? "");
                cmd.Parameters.AddWithValue("@audit_user", auditUser ?? "");
                cmd.Parameters.AddWithValue("@in_tok", meta?.InputTokens ?? 0);
                cmd.Parameters.AddWithValue("@out_tok", meta?.OutputTokens ?? 0);
                cmd.Parameters.AddWithValue("@cache_read", meta?.CacheReadTokens ?? 0);
                cmd.Parameters.AddWithValue("@cache_creation", meta?.CacheCreationTokens ?? 0);
                cmd.Parameters.AddWithValue("@cost", meta?.CostUsd ?? 0m);
                cmd.Parameters.AddWithValue("@latency", meta?.LatencyMs ?? 0);
                cmd.Parameters.AddWithValue("@ok", ok);
                cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);

                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log AI invocation (skill={Skill})", skill);
            }
        });
    }
}
