-- DbRelay SQL Server outbox schema, version 1 (SQL Server 2019+, Azure SQL).
--
-- Idempotent: every statement only creates objects that do not exist yet. Nothing is ever dropped or altered.
-- {{schema}} and {{table}} are replaced with the configured SqlServerOutboxOptions.Schema and TableName
-- (defaults: dbrelay and outbox_messages). IOutboxSchemaManager.GetCreateScript() returns this script with the names filled in.
-- Batches are separated by GO, so the script can be run with sqlcmd or SSMS. Filtered indexes require
-- QUOTED_IDENTIFIER and ANSI_NULLS ON (sqlcmd defaults QUOTED_IDENTIFIER to OFF unless -I is passed).
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'{{schema}}') IS NULL
    EXEC(N'CREATE SCHEMA [{{schema}}]');
GO

-- sequence: clustered, ever-increasing key. Message IDs (UUIDv7 or caller-supplied) do not sort sequentially as
-- uniqueidentifier, so they get a unique nonclustered index instead of the clustered key.
-- All times are UTC from SYSUTCDATETIME(), truncated to milliseconds.
IF OBJECT_ID(N'[{{schema}}].[{{table}}]', N'U') IS NULL
    CREATE TABLE [{{schema}}].[{{table}}] (
        [sequence]         bigint           IDENTITY(1, 1) NOT NULL,
        [id]               uniqueidentifier NOT NULL,
        [message_type]     nvarchar(256)    NOT NULL,
        [destination]      nvarchar(512)    NULL,
        [payload]          varbinary(max)   NOT NULL,
        [content_type]     nvarchar(128)    NOT NULL,
        [headers]          nvarchar(max)    NULL,
        [correlation_id]   nvarchar(128)    NULL,
        [causation_id]     nvarchar(128)    NULL,
        [status]           tinyint          NOT NULL CONSTRAINT [df_{{table}}_status] DEFAULT (0),
        [attempt_count]    int              NOT NULL CONSTRAINT [df_{{table}}_attempt_count] DEFAULT (0),
        [created_at]       datetime2(3)     NOT NULL,
        [scheduled_at]     datetime2(3)     NOT NULL,
        [available_at]     datetime2(3)     NOT NULL,
        [lock_token]       uniqueidentifier NULL,
        [locked_by]        nvarchar(256)    NULL,
        [last_attempt_at]  datetime2(3)     NULL,
        [delivered_at]     datetime2(3)     NULL,
        [dead_lettered_at] datetime2(3)     NULL,
        [last_error]       nvarchar(2000)   NULL,
        CONSTRAINT [pk_{{table}}] PRIMARY KEY CLUSTERED ([sequence]),
        CONSTRAINT [ck_{{table}}_status] CHECK ([status] BETWEEN 0 AND 3)
    );
GO

-- Lookup by message ID (admin, lock-token guarded transitions) and duplicate-ID rejection.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{{schema}}].[{{table}}]') AND [name] = N'ux_{{table}}_id')
    CREATE UNIQUE NONCLUSTERED INDEX [ux_{{table}}_id] ON [{{schema}}].[{{table}}] ([id]);
GO

-- Claim: only claimable rows (Pending, Processing with a lease), ordered by available_at.
-- Queries must use the literal predicate [status] IN (0, 1) (or a subset of it) to match the filter.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{{schema}}].[{{table}}]') AND [name] = N'ix_{{table}}_claim')
    CREATE NONCLUSTERED INDEX [ix_{{table}}_claim] ON [{{schema}}].[{{table}}] ([available_at]) INCLUDE ([status])
        WHERE [status] IN (0, 1);
GO

-- Retention cleanup of delivered rows (literal predicate [status] = 2).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{{schema}}].[{{table}}]') AND [name] = N'ix_{{table}}_delivered')
    CREATE NONCLUSTERED INDEX [ix_{{table}}_delivered] ON [{{schema}}].[{{table}}] ([delivered_at])
        WHERE [status] = 2;
GO

-- Dead-letter inspection and bulk requeue, oldest first (literal predicate [status] = 3).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{{schema}}].[{{table}}]') AND [name] = N'ix_{{table}}_dead_lettered')
    CREATE NONCLUSTERED INDEX [ix_{{table}}_dead_lettered] ON [{{schema}}].[{{table}}] ([dead_lettered_at]) INCLUDE ([message_type])
        WHERE [status] = 3;
GO

IF OBJECT_ID(N'[{{schema}}].[schema_version]', N'U') IS NULL
    CREATE TABLE [{{schema}}].[schema_version] (
        [component]  nvarchar(128) NOT NULL,
        [version]    int           NOT NULL,
        [applied_at] datetime2(3)  NOT NULL CONSTRAINT [df_{{schema}}_schema_version_applied_at] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [pk_{{schema}}_schema_version] PRIMARY KEY CLUSTERED ([component])
    );
GO

IF NOT EXISTS (SELECT 1 FROM [{{schema}}].[schema_version] WHERE [component] = N'outbox:{{table}}')
    INSERT INTO [{{schema}}].[schema_version] ([component], [version]) VALUES (N'outbox:{{table}}', 1);
GO
