-- Table for WhipAI skill invocation logs.
-- Run once against fleet_migration (or the equivalent DB set by CNDW_QA / CNDW
-- env vars) before the first skill call, otherwise InvocationLogger will
-- silently swallow each insert (it's fire-and-forget by design).

CREATE TABLE IF NOT EXISTS `ai_invocations` (
    `id`                     BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
    `skill`                  VARCHAR(64) NOT NULL,
    `version`                VARCHAR(16) NOT NULL,
    `model`                  VARCHAR(64) NOT NULL DEFAULT '',
    `audit_user`             VARCHAR(128) NOT NULL DEFAULT '',
    `input_tokens`           INT UNSIGNED NOT NULL DEFAULT 0,
    `output_tokens`          INT UNSIGNED NOT NULL DEFAULT 0,
    `cache_read_tokens`      INT UNSIGNED NOT NULL DEFAULT 0,
    `cache_creation_tokens`  INT UNSIGNED NOT NULL DEFAULT 0,
    -- cost_usd keeps 6 decimal places so cheap calls (~$0.0006 with cache) are visible.
    `cost_usd`               DECIMAL(10, 6) NOT NULL DEFAULT 0,
    `latency_ms`             INT UNSIGNED NOT NULL DEFAULT 0,
    `ok`                     TINYINT(1)   NOT NULL DEFAULT 0,
    `error_message`          TEXT NULL,
    `created_at`             DATETIME NOT NULL DEFAULT UTC_TIMESTAMP(),
    PRIMARY KEY (`id`),
    -- Dashboard queries usually slice by skill + day; keep that leading
    -- so range scans on created_at stay index-friendly.
    KEY `idx_skill_created` (`skill`, `created_at`),
    KEY `idx_created` (`created_at`),
    KEY `idx_user` (`audit_user`, `created_at`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
