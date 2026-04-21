# WhipAI — Claude-powered skill API
# DriveWhip — AI services bridge
# Last updated: 2026-04-20

## Project Overview

Thin .NET 8 API that exposes Claude (Anthropic) as versioned, named "skills"
so WhipFlow / WhipStart can call it instead of hand-coding UI fragments for
every data shape. First skill: `render-argyle` (generates the HTML card in
WhipFlow's Argyle Information modal from whatever Argyle fields are
available for a given applicant).

**Stack:** .NET 8, raw HttpClient against Anthropic's REST API (no SDK —
same pattern as WhipBridge's other integrations), shared-secret
`x-api-key` auth between WhipBridge and WhipAI. **Stateless — no database.**
Callers (WhipBridge) own persistence via their own stored procedures.

**Pattern:** `POST /api/ai/skill/{name}/invoke` with `{ input, options? }`
→ dispatches to the named skill → skill calls Claude with a cached system
prompt + the user-provided input → returns `{ ok, data, meta, error? }`.

---

## Architecture

### Skills

Each skill is a self-contained class under `Skills/` with three things:

1. **Identity** — `Name`, `Version`, `Description` (exposed via
   `GET /api/ai/skills` for catalog UIs).
2. **System prompt** — Markdown file at
   `Skills/Prompts/{Name}.{Version}.md`. Loaded once at process start by
   `BaseSkill`, marked `cache_control: ephemeral` on the Anthropic side so
   repeated invocations hit the cache (~90% off on input tokens).
3. **`RunAsync` override** — builds the user-content message from the
   request, calls `AnthropicService.InvokeAsync`, shapes the response.

To add a new skill: subclass `BaseSkill`, drop the prompt file under
`Skills/Prompts/`, register the class in `Program.cs`. That's it — the
controller picks it up automatically.

### Services

| File | Responsibility |
|------|----------------|
| `Services/AnthropicService.cs` | Wraps Anthropic's REST API via HttpClient. Handles prompt caching, effort, adaptive thinking, cost estimation, and error → envelope conversion. |
| `Helpers/PiiRedactor.cs` | Strips SSN/DOB/bank fields from skill input before it leaves our process. Field-name denylist; not regex-on-value (too aggressive). |

### Models

| File | Shape |
|------|-------|
| `Models/SkillRequest.cs` | `{ input: JsonElement, options?: { model?, version? } }` |
| `Models/SkillResponse.cs` | `{ ok, data?, error?, meta?: { skill, version, model, tokens, cost, latency } }` |

---

## Defaults

- **Model:** `claude-opus-4-7` (per Claude API skill directive; override per-request via `options.model`)
- **Effort:** `Effort.High` (good balance of cost and quality for intelligence-sensitive work)
- **Thinking:** Adaptive (`ThinkingConfigAdaptive` — Claude decides depth per request)
- **MaxTokens:** 4096 default; raise in skill options if outputs get truncated
- **Fail-open:** `AI_FAIL_OPEN=true` returns 503 when Anthropic is down so frontend falls back to local renderers. Flip to `false` only if you want hard failures.

---

## Environment Variables

| Var | Required | Purpose |
|-----|----------|---------|
| `ANTHROPIC_API_KEY` | **Yes** | Without it, server starts but every invoke returns 503 |
| `WHIPAI_API_KEY` | **Yes** | Shared secret — WhipBridge sends it as `x-api-key` on every call. Must match the value in WhipBridge's env |
| `DEFAULT_MODEL` | No | Override the default Claude model (defaults to `claude-opus-4-7`) |
| `AI_FAIL_OPEN` | No | `true` (default) = return 503 on Anthropic errors; `false` = retry aggressively |
| `LOG_LEVEL` | No | Trace/Debug/Information/Warning/Error/Critical |
| `PORT` | Prod only | HTTP port in prod (defaults to 8081; dev uses launchSettings.json) |

---

## Endpoints

| Endpoint | Auth | Purpose |
|----------|------|---------|
| `GET /health` | None | Infra probe. Always returns `{ ok: true, service, version, time }` |
| `GET /api/ai/skills` | `x-api-key` | Lists all registered skills with version + description |
| `POST /api/ai/skill/{name}/invoke` | `x-api-key` | Runs the named skill with the supplied input |

### Auth model

WhipAI is **server-to-server only** — browsers never call it directly.
Authentication is a shared secret (`WHIPAI_API_KEY`), sent by WhipBridge
as the `x-api-key` header on every request. Chosen over JWT bearer because:

- No browser is involved, so there's no user identity to carry
- The key lives on the server only — can't be exfiltrated via XSS or a
  stolen token from a user's session
- Rotates independently of user sessions
- Matches the pattern WhipBridge already uses for its internal endpoints
  (`INTERNAL_API_TOKEN`, `SIMPLETALK_API_TOKEN`)

The "who is calling" is passed as a field on the request body
(`audit_user` inside `SkillRequest`) purely for logging — it's trusted
because the `x-api-key` already proved the caller is WhipBridge.

---

## First-time setup

1. `cp .env.example .env` and populate `ANTHROPIC_API_KEY` + `WHIPAI_API_KEY`.
2. Drop the production `.skill` bundle (or `.md` prompt) into `Skills/Prompts/` — see the `argyle-driver-review` skill for an example.
3. `dotnet run` — local server on `http://localhost:8081`, Swagger at `/swagger`.

No database setup. WhipAI is stateless — responses are always computed
from scratch and returned to the caller.

---

## Adding a new skill (recipe)

1. Create `Skills/{YourSkill}.cs` subclassing `BaseSkill`. Override `Name`, `Version`, `Description`, and `RunAsync`.
2. Create `Skills/Prompts/{name}.{version}.md` with the system prompt. Aim for ≥4096 tokens so prompt caching applies on Opus 4.7.
3. Register the class as a singleton in `Program.cs` and add it to the `SkillRegistry` array:
   ```csharp
   builder.Services.AddSingleton<YourSkill>();
   builder.Services.AddSingleton<SkillRegistry>(sp =>
       new SkillRegistry(new ISkill[]
       {
           sp.GetRequiredService<RenderArgyleSkill>(),
           sp.GetRequiredService<YourSkill>(),  // <-- add here
       }));
   ```
4. `dotnet build` to verify. New skill shows up under `GET /api/ai/skills` automatically.

---

## Cost tracking

WhipAI itself doesn't persist anything — each response includes a `meta`
block with `inputTokens`, `outputTokens`, `cacheReadTokens`, `costUsd`,
and `latencyMs`. Callers that want a per-invocation audit log should
save that envelope themselves via their own SPs (WhipBridge already
caches `argyle-driver-review` results into `crm_applicant_ai_reports`
via SP `crm_applicant_ai_report_save` — that table can be queried for
cost + model + token stats).

Cost is estimated client-side in `AnthropicService.EstimateCostUsd`
from the public price table (updated 2026-04-15). When Anthropic
changes prices, update that method.

---

## Security notes

- **PII:** `PiiRedactor.RedactArgyleInput` strips SSN / DOB / bank fields
  before anything leaves this process. Never log raw skill inputs.
- **Prompt cache:** the system prompt is cached at Anthropic. Changing the
  prompt (even one byte) invalidates the cache and costs ~1.25× on the
  next call. Version the skill (`v1` → `v2`) instead of silently editing
  the file.
- **Anthropic zero-retention:** Anthropic's API does not train on traffic
  and retains data for at most 30 days for abuse monitoring. Still, don't
  send anything we wouldn't send to any other third-party API.
- **Closed-source:** All dependencies are MIT / Apache / BSD. See the
  upstream SDK licenses; we don't ship Anthropic's code — only reference
  it. No copyleft exposure.

---

## Related services

- **WhipBridge** — main API; `ArgyleHelperController` is what currently
  fetches driver records from Argyle. WhipAI consumes that output; it
  doesn't talk to Argyle directly.
- **WhipFlow** — frontend. `applicants-panel.component.ts` calls WhipAI's
  `render-argyle` skill (to be wired once the skill prompt is finalized).
