using MySql.Data.MySqlClient;

namespace WhipAI.Services;

/// <summary>
/// Minimal MySQL helper — picks the right connection string based on the
/// <c>token_environment</c> header (same pattern as WhipBridge's
/// DataAccessMySQL). WhipAI does NOT execute user-defined SPs; this is
/// reserved for the InvocationLogger.
/// </summary>
public sealed class MySqlService
{
    private readonly IConfiguration _config;
    private readonly ILogger<MySqlService> _logger;

    public MySqlService(IConfiguration config, ILogger<MySqlService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public MySqlConnection OpenConnection(string? tokenEnvironment = null)
    {
        // "dev" → CNDW (DEV DB). Anything else (null, "qa", "prod") → CNDW_QA.
        // Matches WhipBridge's switching logic so env behavior is predictable.
        var envVar = string.Equals(tokenEnvironment, "dev", StringComparison.OrdinalIgnoreCase)
            ? "CNDW"
            : "CNDW_QA";

        var cs = Environment.GetEnvironmentVariable(envVar)
                 ?? _config.GetConnectionString(envVar)
                 ?? "";

        if (string.IsNullOrWhiteSpace(cs))
        {
            throw new InvalidOperationException($"MySQL connection string {envVar} is not configured.");
        }

        var conn = new MySqlConnection(cs);
        conn.Open();
        return conn;
    }
}
