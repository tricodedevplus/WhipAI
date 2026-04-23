using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhipAI.Helpers;

/// <summary>
/// Strips fields we don't want Claude to ever see. The model doesn't need
/// SSN / DOB / bank numbers to render a summary card, so sending them is
/// poor hygiene even though Anthropic's API has zero-retention on traffic.
///
/// Redaction is field-name-based on a conservative denylist — easier to
/// reason about than regex on values, and safe if the Argyle schema gains
/// new fields (new data ships by default; we add it to the list if needed).
/// </summary>
public static class PiiRedactor
{
    /// <summary>
    /// Argyle denylist — aggressive because the driver review is about
    /// employment/earnings. Redacts DOB + full_name because those aren't
    /// needed for the summary and give Claude nothing useful.
    /// </summary>
    private static readonly HashSet<string> RedactedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssn",
        "social_security_number",
        "tax_id",
        "tin",
        "date_of_birth",
        "dob",
        "birth_date",
        "birthdate",
        "bank_account",
        "bank_account_number",
        "routing_number",
        "account_number",
        "card_number",
        "card_last_four",
        "full_name", // we keep first_name / last_name separately — full_name is redundant and often carries middle names / suffixes that aren't needed for rendering
    };

    /// <summary>
    /// Checkr denylist — much smaller than Argyle. The background-review
    /// skill MUST see names + DOB + address to do identity-match scoring
    /// (e.g. "record has high match_confidence via name-only but no DOB —
    /// flag as possible mismatch"). Only truly financial identifiers are
    /// stripped because Checkr payloads never need to contain them.
    /// </summary>
    private static readonly HashSet<string> CheckrRedactedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssn",
        "social_security_number",
        "tax_id",
        "tin",
        "bank_account",
        "bank_account_number",
        "routing_number",
        "account_number",
        "card_number",
        "card_last_four",
    };

    /// <summary>
    /// Returns a deep-cloned version of the Argyle input with sensitive
    /// fields replaced by <c>"[REDACTED]"</c>. Safe on any JSON shape.
    /// </summary>
    public static JsonNode? RedactArgyleInput(JsonElement input)
    {
        var node = JsonNode.Parse(input.GetRawText());
        if (node is null) return null;
        ScrubInPlace(node, RedactedFields);
        return node;
    }

    /// <summary>
    /// Returns a deep-cloned version of the Checkr input with financial
    /// identifiers scrubbed but identity fields (name/DOB/address) kept —
    /// the background-review skill needs those to do identity matching.
    /// </summary>
    public static JsonNode? RedactCheckrInput(JsonElement input)
    {
        var node = JsonNode.Parse(input.GetRawText());
        if (node is null) return null;
        ScrubInPlace(node, CheckrRedactedFields);
        return node;
    }

    /// <summary>
    /// Walks the tree and rewrites redacted fields in-place. Must NOT
    /// reassign a non-string child back onto its parent — System.Text.Json
    /// throws "node already has a parent" because the child already
    /// points at us. Only scalar replacements (swap with "[REDACTED]")
    /// create new values. Takes the denylist as a param so callers can
    /// swap the Argyle vs Checkr sets without branching inside here.
    /// </summary>
    private static void ScrubInPlace(JsonNode node, HashSet<string> denyList)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kvp in obj.ToList())
                {
                    if (denyList.Contains(kvp.Key))
                    {
                        obj[kvp.Key] = "[REDACTED]";
                        continue;
                    }
                    if (kvp.Value is not null)
                    {
                        ScrubInPlace(kvp.Value, denyList);
                    }
                }
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is not null)
                    {
                        ScrubInPlace(arr[i]!, denyList);
                    }
                }
                break;

            // JsonValue (string/number/bool/null) — leaf, nothing to do.
            // We intentionally don't redact by value patterns (e.g. SSN-like
            // strings) — that's too aggressive and catches false positives.
            // Field-name redaction is the contract.
        }
    }
}
