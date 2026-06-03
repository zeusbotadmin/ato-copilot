#!/usr/bin/env bash
# ────────────────────────────────────────────────────────────────────────────
# seed-flankspeed.sh
#
# Seeds the Flankspeed CSP-portfolio demo data so the dashboard cards on the
# /portfolio page render non-zero values for the simulated CSP-Admin.
# Idempotent — re-running is a no-op (deterministic IDs + IF NOT EXISTS).
#
# Runs two SQL scripts in this order:
#   1. reassign-flankspeed-systems.sql
#        Moves the 5 demo RegisteredSystems (and every tenant-scoped
#        dependent row) from the system tenant onto the 3 mission-owner
#        tenants (PEO-790 / PMA 290 / PMS 408).
#   2. seed-flankspeed.sql
#        Inserts 3 Assessments + 4 AuthorizationDecisions + 6 Findings +
#        4 PoamItems + 1 Deviation.
#
# After this script finishes, /api/csp/dashboard/summary returns:
#   systemCount:           5
#   atoStatusCounts:       2 authorized · 1 in process · 1 denied
#   openFindingsBySeverity 1 crit · 3 high · 2 mod · 0 low
#   openPoamCount:         4
#   openDeviationCount:    1
#
# Pre-requisites:
#   * docker compose -f docker-compose.mcp.yml is up (ato-copilot-sql, ato-copilot-mcp).
#   * The 3 demo tenants (PEO-790 / PMA 290 / PMS 408) and the 5 demo systems
#     already exist (created by Onboarding flow or earlier seed steps).
#
# Environment:
#   SQL_SA_PASSWORD    — sa password (read from repo-root .env when unset).
#   ATO_SQL_CONTAINER  — docker container name (default: ato-copilot-sql).
# ────────────────────────────────────────────────────────────────────────────
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Load SQL_SA_PASSWORD from .env if unset. We deliberately do NOT use
# `set -o allexport; . .env` here because docker-compose tolerates
# unquoted values containing spaces (e.g. `KEY=SPIN Agent`) but bash
# parses those as separate commands and fails.
if [ -z "${SQL_SA_PASSWORD:-}" ] && [ -f "$REPO_ROOT/.env" ]; then
  envline="$(grep -E '^[[:space:]]*SQL_SA_PASSWORD=' "$REPO_ROOT/.env" | tail -1 || true)"
  if [ -n "$envline" ]; then
    SQL_SA_PASSWORD="${envline#*=}"
    # Strip optional surrounding single or double quotes.
    SQL_SA_PASSWORD="${SQL_SA_PASSWORD%\"}"; SQL_SA_PASSWORD="${SQL_SA_PASSWORD#\"}"
    SQL_SA_PASSWORD="${SQL_SA_PASSWORD%\'}"; SQL_SA_PASSWORD="${SQL_SA_PASSWORD#\'}"
    export SQL_SA_PASSWORD
  fi
fi

: "${SQL_SA_PASSWORD:?SQL_SA_PASSWORD is required (set in .env or env).}"
ATO_SQL_CONTAINER="${ATO_SQL_CONTAINER:-ato-copilot-sql}"

if ! docker ps --format '{{.Names}}' | grep -qx "$ATO_SQL_CONTAINER"; then
  echo "ERROR: container '$ATO_SQL_CONTAINER' is not running." >&2
  echo "       Start the stack first:" >&2
  echo "         docker compose -f docker-compose.mcp.yml up -d sqlserver" >&2
  exit 1
fi

run_sql_file() {
  local label="$1"
  local file="$2"
  echo "─── $label ────────────────────────────────────────────────"
  docker cp "$file" "$ATO_SQL_CONTAINER:/tmp/seed-flankspeed-step.sql" >/dev/null
  docker exec -i "$ATO_SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -N \
    -d AtoCopilot -i /tmp/seed-flankspeed-step.sql
}

run_sql_file "Step 1 — reassign systems"          "$REPO_ROOT/scripts/reassign-flankspeed-systems.sql"
run_sql_file "Step 2 — seed decisions/findings/POA&Ms/deviations" "$REPO_ROOT/scripts/seed-flankspeed.sql"

echo "─────────────────────────────────────────────────────────────"
echo "Flankspeed seed complete. Hard-refresh the /portfolio page."
