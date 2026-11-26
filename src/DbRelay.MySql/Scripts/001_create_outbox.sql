-- DbRelay MySQL / MariaDB outbox schema, version 1. MySQL 8.0 and later (including Aurora MySQL 3), MariaDB 10.11 and later.
--
-- Idempotent and non-destructive: CREATE TABLE IF NOT EXISTS with the indexes inline, and INSERT IGNORE. Existing objects are
-- never altered or removed, so the script is safe to run repeatedly, including by several instances at once.
--
-- The database (MySQL's schema) must already exist: DbRelay never creates databases. DDL commits implicitly.
--
-- IOutboxSchemaManager.GetCreateScript() returns this script with the placeholders below replaced by the configured, validated
-- names. To run it by hand, replace:
--   {{table}}          quoted, optionally database-qualified outbox table   (default `outbox_messages`)
--   {{table_name}}     outbox table name                                    (default outbox_messages)
--   {{version_table}}  quoted, optionally database-qualified version table  (default `dbrelay_schema_version`)
--   {{ck_status}}      status check constraint name                         (default ck_outbox_messages_status)
--   {{ix_claim}}       claim index name                                     (default ix_outbox_messages_claim)
--   {{ix_cleanup}}     retention cleanup index name                         (default ix_outbox_messages_cleanup)
--   {{ix_dead}}        dead-letter index name                               (default ix_outbox_messages_dead)
--
-- Neither engine has partial indexes. The indexes are on virtual generated columns that are NULL for rows of other statuses, and
-- DbRelay's statements filter and order on those columns, so each lookup reads only the rows it needs.

CREATE TABLE IF NOT EXISTS {{table}} (
    id               BINARY(16)   NOT NULL,
    message_type     VARCHAR(256) NOT NULL,
    destination      VARCHAR(512) NULL,
    payload          LONGBLOB     NOT NULL,
    content_type     VARCHAR(128) NOT NULL,
    headers          MEDIUMTEXT   NULL,
    correlation_id   VARCHAR(128) NULL,
    causation_id     VARCHAR(128) NULL,
    status           TINYINT      NOT NULL DEFAULT 0,
    attempt_count    INT          NOT NULL DEFAULT 0,
    created_at       DATETIME(6)  NOT NULL,
    scheduled_at     DATETIME(6)  NOT NULL,
    available_at     DATETIME(6)  NOT NULL,
    lock_token       BINARY(16)   NULL,
    locked_by        VARCHAR(256) NULL,
    last_attempt_at  DATETIME(6)  NULL,
    delivered_at     DATETIME(6)  NULL,
    dead_lettered_at DATETIME(6)  NULL,
    last_error       MEDIUMTEXT   NULL,
    claim_at         DATETIME(6)  AS (CASE WHEN status IN (0, 1) THEN available_at END) VIRTUAL,
    cleanup_at       DATETIME(6)  AS (CASE WHEN status = 2 THEN delivered_at END) VIRTUAL,
    dead_at          DATETIME(6)  AS (CASE WHEN status = 3 THEN dead_lettered_at END) VIRTUAL,
    PRIMARY KEY (id),
    -- Claiming: due pending and leased rows, oldest available_at first. status splits the statistics count from the index.
    KEY {{ix_claim}} (claim_at, status),
    -- Retention cleanup of delivered rows.
    KEY {{ix_cleanup}} (cleanup_at),
    -- Dead-letter inspection and bulk requeue (oldest first).
    KEY {{ix_dead}} (dead_at),
    CONSTRAINT {{ck_status}} CHECK (status BETWEEN 0 AND 3)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_bin;

CREATE TABLE IF NOT EXISTS {{version_table}} (
    component  VARCHAR(128) NOT NULL,
    version    INT          NOT NULL,
    applied_at DATETIME(6)  NOT NULL,
    PRIMARY KEY (component)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_bin;

INSERT IGNORE INTO {{version_table}} (component, version, applied_at) VALUES ('outbox:{{table_name}}', 1, UTC_TIMESTAMP(6));
