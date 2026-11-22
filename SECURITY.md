# Security policy

## Supported versions

DbRelay is pre-1.0. Security fixes are made on the latest `0.x` release only.

| Version | Supported |
|---|---|
| 0.1.x (unreleased) | Yes |

## Reporting a vulnerability

**Please do not report security vulnerabilities in public issues, discussions or pull requests.**

Use GitHub's private vulnerability reporting: open the repository's **Security** tab and choose **Report a vulnerability**,
or go directly to <https://github.com/Manish-1kumar/DbRelay/security/advisories/new>. This keeps the report private between
you and the maintainers until a fix is released.

<!-- TODO(maintainer): private vulnerability reporting can only be enabled once the repository is public (Settings > Code security). Enable it at the same time as making the repository public. -->
<!-- TODO(maintainer): add a fallback security contact for reporters who cannot use GitHub. -->

Please include:

- the DbRelay version, the provider (PostgreSQL or SQL Server) and the database version;
- a description of the issue and its impact;
- the steps or a minimal project needed to reproduce it.

We aim to acknowledge reports within 5 working days and to agree a disclosure timeline with you. Reporters are credited in
the release notes unless they prefer not to be.

## Scope and design notes

These areas are especially relevant to DbRelay's threat model:

- **SQL injection.** Every value is a parameter. Schema and table names come only from configuration. They must consist
  solely of an ASCII letter or underscore followed by up to 62 ASCII letters, digits or underscores; whitespace and control
  characters, including a trailing newline, are rejected. They are always quoted.
- **Type activation.** DbRelay never creates a CLR type from a name stored in the database. Handlers name the target type
  explicitly when they deserialize.
- **Secrets in logs.** DbRelay never logs payloads or connection strings. A malformed PostgreSQL connection string is rejected
  without echoing it.
- **Error text.** Handler exception messages and `DeliveryResult` reasons are stored in the `last_error` column (truncated to
  2,000 characters) and logged. **If your handler puts payload values or secrets into exception messages or reasons, they
  will be persisted and logged.** Keep reasons short and free of sensitive data.
- **Database permissions.** At runtime DbRelay needs `SELECT`, `INSERT`, `UPDATE` and `DELETE` on its outbox table. It needs
  DDL rights only if you call `EnsureCreatedAsync`. See [docs/postgres.md](docs/postgres.md) and
  [docs/sqlserver.md](docs/sqlserver.md).
