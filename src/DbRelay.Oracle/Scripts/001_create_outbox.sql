-- DbRelay Oracle outbox schema, version 1. Oracle Database 19c and later.
--
-- Idempotent and non-destructive: each object is created in its own EXECUTE IMMEDIATE, and "already exists" errors
-- (ORA-00955, ORA-01408, ORA-02260, ORA-02261, ORA-02264) are ignored. Existing objects are never altered or removed, so the
-- script is safe to run repeatedly, including by several instances at once. Text columns are NVARCHAR2 and NCLOB, which are
-- Unicode whatever the database character set.
--
-- The schema (user) must already exist: DbRelay never creates users. Run as that user, or as a user that may create tables
-- and indexes in it. Oracle commits DDL implicitly.
--
-- IOutboxSchemaManager.GetCreateScript() returns this script with the placeholders below replaced by the configured,
-- validated, upper-case names. To run it by hand (for example in SQL*Plus or SQLcl), replace:
--   {{table}}          quoted, optionally schema-qualified outbox table   (default "OUTBOX_MESSAGES")
--   {{table_name}}     outbox table name                                  (default OUTBOX_MESSAGES)
--   {{version_table}}  quoted, optionally schema-qualified version table  (default "DBRELAY_SCHEMA_VERSION")
--   {{pk}}             primary key constraint name                        (default PK_OUTBOX_MESSAGES)
--   {{ck_status}}      status check constraint name                       (default CK_OUTBOX_MESSAGES_STATUS)
--   {{ck_id}}          ID length check constraint name                    (default CK_OUTBOX_MESSAGES_ID)
--   {{ix_claim}}       quoted, optionally schema-qualified claim index    (default "IX_OUTBOX_MESSAGES_CLAIM")
--   {{ix_cleanup}}     quoted, optionally schema-qualified cleanup index  (default "IX_OUTBOX_MESSAGES_CLEANUP")
--   {{ix_dead}}        quoted, optionally schema-qualified dead index     (default "IX_OUTBOX_MESSAGES_DEAD")
--
-- Oracle has no partial indexes. The indexes are function-based: rows of other statuses map to NULL keys, and a key that is
-- entirely NULL is not stored, so each index holds only the rows its query needs. DbRelay's statements repeat the exact same
-- CASE expressions so that the optimizer can use them.
DECLARE
    PROCEDURE run(p_sql IN VARCHAR2) IS
    BEGIN
        EXECUTE IMMEDIATE p_sql;
    EXCEPTION
        WHEN OTHERS THEN
            IF SQLCODE NOT IN (-955, -1408, -2260, -2261, -2264) THEN
                RAISE;
            END IF;
    END;
BEGIN
    -- No session settings are changed: the session may be pooled and shared with the application. EnsureCreatedAsync retries
    -- the whole script when a concurrent instance's DDL holds a lock (ORA-00054, ORA-04020, ORA-04021).
    run('CREATE TABLE {{table}} (
        ID               RAW(16)            NOT NULL,
        MESSAGE_TYPE     NVARCHAR2(256)     NOT NULL,
        DESTINATION      NVARCHAR2(512)     NULL,
        PAYLOAD          BLOB               NOT NULL,
        CONTENT_TYPE     NVARCHAR2(128)     NOT NULL,
        HEADERS          NCLOB              NULL,
        CORRELATION_ID   NVARCHAR2(128)     NULL,
        CAUSATION_ID     NVARCHAR2(128)     NULL,
        STATUS           NUMBER(1)          DEFAULT 0 NOT NULL,
        ATTEMPT_COUNT    NUMBER(10)         DEFAULT 0 NOT NULL,
        CREATED_AT       TIMESTAMP(6)       DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
        SCHEDULED_AT     TIMESTAMP(6)       NOT NULL,
        AVAILABLE_AT     TIMESTAMP(6)       NOT NULL,
        LOCK_TOKEN       RAW(16)            NULL,
        LOCKED_BY        NVARCHAR2(256)     NULL,
        LAST_ATTEMPT_AT  TIMESTAMP(6)       NULL,
        DELIVERED_AT     TIMESTAMP(6)       NULL,
        DEAD_LETTERED_AT TIMESTAMP(6)       NULL,
        LAST_ERROR       NCLOB              NULL,
        CONSTRAINT "{{pk}}" PRIMARY KEY (ID),
        CONSTRAINT "{{ck_status}}" CHECK (STATUS BETWEEN 0 AND 3),
        CONSTRAINT "{{ck_id}}" CHECK (LENGTH(RAWTOHEX(ID)) = 32)
    )');

    -- Claiming: due pending and leased rows, oldest AVAILABLE_AT first. The second key column splits the statistics count.
    run('CREATE INDEX {{ix_claim}} ON {{table}} (CASE WHEN STATUS IN (0, 1) THEN AVAILABLE_AT END, CASE WHEN STATUS IN (0, 1) THEN STATUS END)');

    -- Retention cleanup of delivered rows.
    run('CREATE INDEX {{ix_cleanup}} ON {{table}} (CASE WHEN STATUS = 2 THEN DELIVERED_AT END)');

    -- Dead-letter inspection and bulk requeue (oldest first).
    run('CREATE INDEX {{ix_dead}} ON {{table}} (CASE WHEN STATUS = 3 THEN DEAD_LETTERED_AT END)');

    run('CREATE TABLE {{version_table}} (
        COMPONENT  VARCHAR2(128 CHAR) NOT NULL,
        VERSION    NUMBER(10)         NOT NULL,
        APPLIED_AT TIMESTAMP(6)       DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
        CONSTRAINT "PK_DBRELAY_SCHEMA_VERSION" PRIMARY KEY (COMPONENT)
    )');

    BEGIN
        EXECUTE IMMEDIATE 'INSERT INTO {{version_table}} (COMPONENT, VERSION) SELECT ''outbox:{{table_name}}'', 1 FROM DUAL '
            || 'WHERE NOT EXISTS (SELECT 1 FROM {{version_table}} WHERE COMPONENT = ''outbox:{{table_name}}'')';
    EXCEPTION
        WHEN DUP_VAL_ON_INDEX THEN
            NULL;
    END;

    COMMIT;
END;
/
