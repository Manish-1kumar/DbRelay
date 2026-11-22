## Summary

<!-- What does this change and why? Link the issue it addresses. -->

## Checklist

- [ ] `dotnet build -c Release` passes with no warnings.
- [ ] `dotnet test -c Release` passes. Database tests ran against a real database (Testcontainers or `DBRELAY_TEST_POSTGRES` / `DBRELAY_TEST_SQLSERVER`).
      If database tests were **skipped**, say so here: <!-- e.g. "SQL Server tests skipped: no instance available" -->
- [ ] `dotnet format --verify-no-changes` passes.
- [ ] New behaviour has tests, and no existing test was weakened.
- [ ] Public API changes have XML docs and are explained below (the public surface is kept deliberately small).
- [ ] Provider changes pass the shared conformance suite (`tests/DbRelay.Storage.Conformance`).
- [ ] Docs, samples and `CHANGELOG.md` are updated where behaviour or API changed.

## Invariants

Confirm the change keeps every invariant in `CLAUDE.md`, in particular:

- [ ] DbRelay never commits, rolls back or disposes a caller-owned connection or transaction.
- [ ] No database transaction is open while a delivery handler runs.
- [ ] Every state change after a claim is guarded by the lock token.
- [ ] Lease and availability comparisons use the database clock.
- [ ] Nothing documents or implies exactly-once delivery.
- [ ] All SQL is parameterized; identifiers come only from validated configuration.
- [ ] No payloads or connection strings are logged.

## Test evidence

<!-- Paste the test summary lines (total / failed / succeeded / skipped) for the projects you ran. -->
