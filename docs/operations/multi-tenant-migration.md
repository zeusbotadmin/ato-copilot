# Multi-Tenant Migration Runbook

> **Spec**: [`specs/048-tenant-isolation/spec.md`](../../specs/048-tenant-isolation/spec.md) · **Architecture**: [`docs/architecture/tenant-isolation.md`](../architecture/tenant-isolation.md)

## Audience

Operations engineers migrating an existing **SingleTenant** ATO Copilot
deployment to **MultiTenant** mode. This is a one-way operation: rolling back
requires a database restore.

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Pre-Flight Checklist](#pre-flight-checklist)
3. [Migration Steps](#migration-steps)
4. [Rollback](#rollback)
5. [Troubleshooting](#troubleshooting)
6. [FAQ](#faq)

---

## Prerequisites

- SQL Server 2019+ (production). SQLite cannot enable Row-Level Security and is
  not supported as a migration source.
- A user account with `db_owner` on the target database (the migration installs
  RLS policies and `SECURITY POLICY` objects).
- A user account with `CSP.Admin` role for any UI/HTTP path; the CLI tool
  authenticates against the database directly and bypasses Entra.
- A current backup of the target database (see §3.1).
- The current build of `ato-cli` deployed on the migration host.

## Pre-Flight Checklist

Run all of these before §3.

- [ ] Application is in maintenance mode (no inbound traffic).
- [ ] Recent backup exists and has been **restore-tested** to a scratch DB.
- [ ] `appsettings.Production.json` has both `Deployment:Mode` (currently
      `SingleTenant`) and the connection string available for editing.
- [ ] At least one row exists in the target `Tenants` table corresponding to
      the migrated default. If none exists, create it via
      `POST /api/admin/seed-default-tenant` or run
      `ato-cli tenant create --display-name "<your CSP>"` and capture the
      resulting GUID for `--default-tenant-id`.
- [ ] If you have multiple legal entities sharing one DB, prepare a CSV
      mapping file (see §3.2). Otherwise the default tenant id covers
      everything.

## Migration Steps

### 3.1 Backup the Database

```pwsh
sqlcmd -S <server> -d master -Q "BACKUP DATABASE [AtoCopilot] TO DISK='X:\backups\AtoCopilot-pre-mt.bak' WITH COMPRESSION, INIT"
```

Verify the backup is readable:

```pwsh
sqlcmd -S <server> -d master -Q "RESTORE VERIFYONLY FROM DISK='X:\backups\AtoCopilot-pre-mt.bak'"
```

### 3.2 Dry Run / Preview

The migration tool exposes a **preview** mode that lists the per-table row
counts and the chosen tenant assignment without writing anything.

**Via HTTP** (CSP-Admin only):

```http
GET /api/admin/migrate-to-multitenant/preview
Authorization: Bearer <csp-admin-token>
```

**Via CLI** (air-gapped):

```bash
ato-cli tenant migrate \
  --connection-string "Server=...;Database=AtoCopilot;..." \
  --default-tenant-id 11111111-1111-1111-1111-111111111111 \
  --csv ./tenant-overrides.csv \
  --install-rls=false \
  --report-out ./preview.json
```

> The CLI does not have a separate `--dry-run` flag; running with
> `--install-rls=false` and inspecting the report before the second run is the
> recommended dry-run pattern. The HTTP `/preview` endpoint is the no-write
> equivalent.

`tenant-overrides.csv` columns (no header required; header line ignored if
present):

```csv
TableName,RowIdPrefix,TenantId
RegisteredSystems,prod-,22222222-2222-2222-2222-222222222222
Persons,contractor-,33333333-3333-3333-3333-333333333333
```

The `RowIdPrefix` is matched against the row's primary key as a string prefix.
Rows that match no override fall back to `--default-tenant-id`.

### 3.3 Execute

**Via HTTP**:

```http
POST /api/admin/migrate-to-multitenant
Authorization: Bearer <csp-admin-token>
Content-Type: application/json

{
  "defaultTenantId": "11111111-1111-1111-1111-111111111111",
  "overrides": [
    { "tableName": "RegisteredSystems", "rowIdPrefix": "prod-", "tenantId": "22222222-2222-2222-2222-222222222222" }
  ],
  "installRls": true
}
```

**Via CLI**:

```bash
ato-cli tenant migrate \
  --connection-string "Server=...;Database=AtoCopilot;..." \
  --default-tenant-id 11111111-1111-1111-1111-111111111111 \
  --csv ./tenant-overrides.csv \
  --install-rls=true \
  --report-out ./migration-report.json
```

The output `migration-report.json` is the canonical record of what was
written. Archive it next to the backup file. Both the HTTP endpoint and the
CLI emit an `AuditLogEntry` with the action verb and a correlation id.

### 3.4 Verify Tenant Isolation

After the run, before reopening traffic:

1. Pick two tenant ids from `Tenants`. Run a control query as each:

   ```sql
   EXEC sp_set_session_context 'TenantId', '<tenant-A-guid>';
   SELECT COUNT(*) FROM RegisteredSystems;
   EXEC sp_set_session_context 'TenantId', '<tenant-B-guid>';
   SELECT COUNT(*) FROM RegisteredSystems;
   ```

   The two counts MUST match the report's per-tenant rollup. A non-CSP-Admin
   session that omits `sp_set_session_context` MUST return 0 rows from any
   `[TenantScoped]` table.

2. Run the integration smoke test pack against the migrated DB:

   ```bash
   dotnet test tests/Ato.Copilot.Tests.Integration --filter "FullyQualifiedName~Tenancy"
   ```

   All Tenancy-marked tests MUST pass against the production DB connection
   string before §3.5.

### 3.5 Switch Mode

Edit `appsettings.Production.json`:

```json
{
  "Deployment": { "Mode": "MultiTenant" }
}
```

Restart the application. On first request, `TenantResolutionMiddleware`
validates that every authenticated principal carries a `tid` claim; legacy
clients that previously relied on the implicit single-tenant binding now
receive `400 MISSING_TENANT_CLAIM`.

If `MultiTenant` is on but no `CspProfile` row is `Active`, the CSP-Admin who
signs in next is routed through the singleton onboarding wizard
(`/onboarding/csp`) per Feature 048 §Phase 13. All other endpoints return
`503 CSP_ONBOARDING_INCOMPLETE` until the wizard completes.

### 3.6 Enable Row-Level Security (SQL Server)

Step 3.3 with `installRls=true` already does this. To verify:

```sql
SELECT name, is_enabled, is_not_for_replication
FROM sys.security_policies
WHERE name LIKE 'sp_TenantIsolation_%';
```

Each `[TenantScoped]` table should appear in the results with `is_enabled = 1`.

## Rollback

There is no in-place rollback. The supported procedure is:

1. Take the application offline.
2. Restore the database from the backup taken in §3.1.
3. Revert `Deployment:Mode` back to `SingleTenant` in
   `appsettings.Production.json`.
4. Restart.
5. Restore from the audit-log archive: any rows added between the backup and
   rollback are lost.

This is intentional. Multi-tenant rows reference cross-tenant primary keys
(impersonation cookies, audit log, global baselines) that cannot be unwound
with a forward-only script.

## Troubleshooting

| Symptom                                      | Likely cause                          | Action |
|----------------------------------------------|---------------------------------------|--------|
| `MIGRATION_FAILED: tenant table is empty`    | No `Tenants` row before run           | Pre-flight §3 — create one first |
| `400 INVALID_REQUEST: defaultTenantId is required` | Missing body field              | Add `defaultTenantId` |
| `403 FORBIDDEN_NOT_CSP_ADMIN`                | Caller lacks `CSP.Admin` role         | Use a CSP-Admin token or the CLI |
| Report shows `error: "RLS install failed: ..."` | DB user not `db_owner`             | Re-run with `db_owner` credentials |
| Post-migration: any tenant sees 0 rows in dashboard | `sp_set_session_context` not set | Confirm app restart picked up `Deployment:Mode = MultiTenant` |
| `503 CSP_ONBOARDING_INCOMPLETE` after restart | Expected — CSP-Admin must finish wizard | Sign in as `CSP.Admin` and complete `/onboarding/csp` |

## FAQ

**Q: Can I run the migration while the application is online?**
A: No. The interceptor stamps `TenantId` on writes, but the schema change to
non-null `TenantId` columns and the RLS policy install both require an
exclusive table lock window. Take the app offline.

**Q: What happens to existing `AuditLogEntry` rows?**
A: They are `[GlobalReference]`, so they are not touched by the backfill.
They remain queryable from any tenant that has audit-read permission.

**Q: I see the migration ran twice; is that bad?**
A: No. The migration service is idempotent: it skips tables where
`TenantId` is already non-null on every row, and `CREATE SECURITY POLICY` is
wrapped in `IF NOT EXISTS`.

**Q: Can I split one tenant into two later?**
A: Yes, via `POST /api/admin/tenants/split` — but that is a separate runbook
(not part of Feature 048).

