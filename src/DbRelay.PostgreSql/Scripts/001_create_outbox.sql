-- DbRelay PostgreSQL outbox schema, version 1.
--
-- Idempotent and non-destructive: every statement uses IF NOT EXISTS / ON CONFLICT DO NOTHING, and existing objects are
-- never altered or removed, so the script is safe to run repeatedly.
--
-- IOutboxSchemaManager.GetCreateScript() returns this script with the placeholders below replaced by the configured,
-- validated names. To run it by hand, replace:
--   {{schema}}      schema name                    (default dbrelay)
--   {{table}}       outbox table name              (default outbox_messages)
--   {{ck_status}}   status check constraint name   (default ck_outbox_messages_status)
--   {{ix_claim}}    claim index name               (default ix_outbox_messages_claim)
--   {{ix_cleanup}}  retention cleanup index name   (default ix_outbox_messages_cleanup)
--   {{ix_dead}}     dead-letter index name         (default ix_outbox_messages_dead)

CREATE SCHEMA IF NOT EXISTS "{{schema}}";

CREATE TABLE IF NOT EXISTS "{{schema}}"."{{table}}" (
    id               uuid         PRIMARY KEY,
    message_type     text         NOT NULL,
    destination      text         NULL,
    payload          bytea        NOT NULL,
    content_type     text         NOT NULL,
    headers          jsonb        NULL,
    correlation_id   text         NULL,
    causation_id     text         NULL,
    status           smallint     NOT NULL DEFAULT 0,
    attempt_count    integer      NOT NULL DEFAULT 0,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    scheduled_at     timestamptz  NOT NULL,
    available_at     timestamptz  NOT NULL,
    lock_token       uuid         NULL,
    locked_by        text         NULL,
    last_attempt_at  timestamptz  NULL,
    delivered_at     timestamptz  NULL,
    dead_lettered_at timestamptz  NULL,
    last_error       text         NULL,
    CONSTRAINT "{{ck_status}}" CHECK (status BETWEEN 0 AND 3)
);

-- Claiming: status IN (0, 1) AND available_at <= now() ORDER BY available_at. Pending and leased rows share one range.
CREATE INDEX IF NOT EXISTS "{{ix_claim}}" ON "{{schema}}"."{{table}}" (available_at) WHERE status IN (0, 1);

-- Retention cleanup of delivered rows.
CREATE INDEX IF NOT EXISTS "{{ix_cleanup}}" ON "{{schema}}"."{{table}}" (delivered_at) WHERE status = 2;

-- Dead-letter inspection and bulk requeue (oldest first).
CREATE INDEX IF NOT EXISTS "{{ix_dead}}" ON "{{schema}}"."{{table}}" (dead_lettered_at) WHERE status = 3;

CREATE TABLE IF NOT EXISTS "{{schema}}".schema_version (
    component  text         PRIMARY KEY,
    version    integer      NOT NULL,
    applied_at timestamptz  NOT NULL DEFAULT now()
);

INSERT INTO "{{schema}}".schema_version (component, version) VALUES ('outbox:{{table}}', 1) ON CONFLICT (component) DO NOTHING;
