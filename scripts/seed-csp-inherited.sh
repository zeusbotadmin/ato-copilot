#!/usr/bin/env bash
# ────────────────────────────────────────────────────────────────────────────
# seed-csp-inherited.sh
#
# Idempotently seeds the CSP-inherited components library (Feature 048 / US9).
# Each component is created at CSP scope (CSP-Admin home tenant) via
# `POST /api/csp/inherited-components` and linked to one or more capabilities
# via `POST /api/csp/inherited-components/{id}/capabilities`. Manual creates
# land as `Published` (CspInheritedComponentService.CreateAsync) so every
# tenant inherits them immediately — no separate `/publish` step required.
#
# Re-runs are safe: components/capabilities are looked up by exact name and
# skipped when they already exist.
#
# Environment:
#   $ATO_BASE_URL     — full base URL (incl. /api). Overrides everything else.
#   $ATO_SERVER_PORT  — port only; URL built as http://localhost:$PORT/api.
#                       Defaults to value in repo-root .env, then 3002.
#
# Pre-requisites:
#   - MCP container running (docker compose up ato-copilot)
#   - Simulated CSP-Admin identity active (appsettings.Development.json:
#     SimulatedRoles includes "CSP.Admin")
#   - CSP profile state = Active (see scripts/seed-systems.sh)
# ────────────────────────────────────────────────────────────────────────────
set -euo pipefail

if [ -z "${ATO_BASE_URL:-}" ]; then
  if [ -z "${ATO_SERVER_PORT:-}" ] && [ -f "$(dirname "$0")/../.env" ]; then
    # shellcheck disable=SC1090,SC2046
    set -o allexport; . "$(dirname "$0")/../.env"; set +o allexport || true
  fi
  ATO_BASE_URL="http://localhost:${ATO_SERVER_PORT:-3002}/api"
fi

BASE="${ATO_BASE_URL%/}/csp/inherited-components"
H_CT="Content-Type: application/json"

echo "Seeding CSP-inherited components via $BASE"

# ── Pre-flight: confirm endpoint is reachable + CSP profile is Active ─────
preflight() {
  local out status
  out=$(curl -sS -o /tmp/csp_preflight.json -w '%{http_code}' "${BASE}?pageSize=1") || {
    echo "✗ Could not reach $BASE — is the MCP container up?" >&2; exit 2; }
  status="$out"
  case "$status" in
    200) ;;
    403)
      echo "✗ 403 from $BASE — simulated identity is not a CSP.Admin." >&2
      echo "  Check src/Ato.Copilot.Mcp/appsettings.Development.json → SimulatedRoles." >&2
      exit 2 ;;
    404)
      echo "✗ 404 SINGLE_TENANT_MODE — Deployment.Mode must be MultiTenant." >&2
      exit 2 ;;
    503)
      echo "✗ 503 CSP_ONBOARDING_INCOMPLETE — run scripts/seed-systems.sh first." >&2
      exit 2 ;;
    *)
      echo "✗ Unexpected pre-flight status $status from $BASE" >&2
      cat /tmp/csp_preflight.json >&2 || true
      exit 2 ;;
  esac
}

# ── Lookup an existing component by exact name (echo GUID or "") ──────────
find_component_id() {
  local name="$1"
  python3 - "$name" "$BASE" <<'PY' 2>/dev/null || true
import json, sys, urllib.request, urllib.error
name, base = sys.argv[1], sys.argv[2]
try:
    with urllib.request.urlopen(f"{base}?pageSize=500") as r:
        env = json.load(r)
except Exception:
    sys.exit(0)
items = (env.get("data") or {}).get("items") or []
for it in items:
    if it.get("name") == name:
        print(it.get("id", "")); sys.exit(0)
PY
}

# ── Create a component (idempotent on name). Echoes GUID. ─────────────────
create_or_get_component() {
  local name="$1"; local desc="$2"; local ctype="$3"
  local existing
  existing=$(find_component_id "$name")
  if [ -n "$existing" ]; then
    echo "$existing"; return 0
  fi
  local body
  body=$(python3 -c '
import json, sys
print(json.dumps({"name": sys.argv[1], "description": sys.argv[2], "componentType": sys.argv[3]}))
' "$name" "$desc" "$ctype")
  local resp
  resp=$(curl -sS -X POST "$BASE" -H "$H_CT" -d "$body")
  echo "$resp" | python3 -c '
import json, sys
env = json.load(sys.stdin)
if env.get("status") != "success":
    sys.stderr.write(f"create component failed: {json.dumps(env)}\n")
    sys.exit(1)
print(env["data"]["id"])
'
}

# ── Lookup an existing capability by exact name (echo GUID or "") ─────────
find_capability_id() {
  local cid="$1"; local cap_name="$2"
  python3 - "$BASE" "$cid" "$cap_name" <<'PY' 2>/dev/null || true
import json, sys, urllib.request
base, cid, name = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    with urllib.request.urlopen(f"{base}/{cid}/capabilities") as r:
        env = json.load(r)
except Exception:
    sys.exit(0)
caps = env.get("data") or []
for c in caps:
    if c.get("name") == name:
        print(c.get("id", "")); sys.exit(0)
PY
}

# ── Add a capability (idempotent on name) ─────────────────────────────────
add_capability() {
  local cid="$1"; local cap_name="$2"; local cap_desc="$3"; local controls_csv="$4"
  local existing
  existing=$(find_capability_id "$cid" "$cap_name")
  if [ -n "$existing" ]; then
    echo "    ↺ $cap_name (exists)"
    return 0
  fi
  local body
  body=$(python3 -c '
import json, sys
print(json.dumps({
  "name": sys.argv[1],
  "description": sys.argv[2],
  "mappedNistControlIds": [c.strip() for c in sys.argv[3].split(",") if c.strip()],
}))
' "$cap_name" "$cap_desc" "$controls_csv")
  local resp
  resp=$(curl -sS -X POST "$BASE/$cid/capabilities" -H "$H_CT" -d "$body")
  local newid
  newid=$(echo "$resp" | python3 -c '
import json, sys
env = json.load(sys.stdin)
if env.get("status") != "success":
    sys.stderr.write(f"add capability failed: {json.dumps(env)}\n")
    sys.exit(1)
print(env["data"]["id"])
') || return 1
  echo "    + $cap_name → $newid   [$controls_csv]"
}

# ── Top-level helper: seed one component + its capabilities ───────────────
# Usage: seed "<name>" "<componentType>" "<description>" \
#             "<cap1_name>" "<cap1_desc>" "<cap1_ctrls_csv>" \
#             "<cap2_name>" "<cap2_desc>" "<cap2_ctrls_csv>" ...
seed() {
  local name="$1"; local ctype="$2"; local desc="$3"
  shift 3
  echo "  • $name ($ctype)"
  local cid
  cid=$(create_or_get_component "$name" "$desc" "$ctype")
  echo "    component: $cid"
  while [ $# -gt 0 ]; do
    local cap_name="$1"; local cap_desc="$2"; local cap_ctrls="$3"
    shift 3
    add_capability "$cid" "$cap_name" "$cap_desc" "$cap_ctrls"
  done
}

# ──────────────────────────────── Main ────────────────────────────────────
preflight

echo "=== Creating CSP-inherited components + capabilities (idempotent) ==="

seed "Microsoft Entra ID" "Identity" \
  "Cloud identity & access management — SSO, MFA, conditional access, PIM." \
  "Multi-Factor Authentication" \
    "Phishing-resistant MFA via Authenticator, FIDO2, and CAC/PIV smart-cards enforced through Entra Conditional Access." \
    "AC-2,AC-7,IA-2,IA-5" \
  "Role-Based Access Control" \
    "Least-privilege access via Entra ID RBAC roles + PIM just-in-time elevation with periodic access reviews." \
    "AC-2,AC-3,AC-6"

seed "Microsoft Sentinel" "Service" \
  "Cloud-native SIEM/SOAR — log aggregation, correlation, and automated incident response." \
  "Security Information & Event Management" \
    "Centralized audit log ingestion + analysis across Azure, Entra, M365, and on-prem connectors." \
    "AU-2,AU-3,AU-6,SI-4" \
  "Security Orchestration & Automated Response" \
    "Playbook-driven incident response with ServiceNow / Teams integration and automated containment." \
    "IR-4,IR-5"

seed "Microsoft Defender for Cloud" "Service" \
  "Unified CSPM + CWPP — continuous posture assessment, regulatory compliance, workload protection." \
  "Continuous Security Assessment" \
    "Automated CSPM scoring + Defender for Cloud regulatory compliance dashboard with secure-score trending." \
    "CA-2,CA-7,RA-5" \
  "Vulnerability Management" \
    "Continuous OS / container / app vulnerability scanning prioritized by exploitability and asset criticality." \
    "RA-5,SI-2"

seed "Azure Key Vault" "Service" \
  "Secrets / keys / certificate management — FIPS 140-2 Level 2 HSMs, fully audit-logged." \
  "Certificate-Based Authentication" \
    "X.509 and TLS certificate lifecycle management with HSM-backed keys, automated rotation, and expiry alerts." \
    "IA-5,SC-12" \
  "Data Encryption (At-Rest & In-Transit)" \
    "Customer-managed encryption keys + TLS 1.2+ enforcement across storage, SQL TDE, and service bus." \
    "SC-8,SC-13,SC-28"

seed "Azure Firewall" "Network" \
  "Cloud-native L3-L7 firewall with threat intelligence, TLS inspection, and centralized policy management." \
  "Network Segmentation & Filtering" \
    "Defense-in-depth with NSGs, private endpoints, and deny-by-default rules; centrally authored policy." \
    "SC-7,AC-4"

seed "Microsoft Defender for Endpoint" "Service" \
  "Enterprise endpoint EDR — detection, automated investigation, attack-surface reduction, TVM." \
  "Endpoint Detection & Response" \
    "Real-time endpoint threat detection + automated investigation across Windows, Linux, and macOS endpoints." \
    "SI-3,SI-4"

seed "Azure Backup & Site Recovery" "Service" \
  "Automated backup + geo-redundant disaster recovery — cross-region failover, RPO < 15m, RTO < 1h." \
  "Backup & Disaster Recovery" \
    "Policy-driven backup with geo-redundant storage and quarterly DR-failover tests." \
    "CP-9,CP-10"

seed "Azure Policy Engine" "Platform" \
  "Policy-as-code — deny / audit / auto-remediation enforced at every resource deployment." \
  "Infrastructure Policy Enforcement" \
    "Centrally authored guardrails enforced on every ARM/Bicep deployment with drift auto-remediation." \
    "CM-2,CM-6"

seed "Azure Government Cloud Region" "Infrastructure" \
  "FedRAMP High / DoD IL5 accredited hosting infrastructure (US Gov Virginia, Texas, Arizona regions)." \
  "Physical & Environmental Protection" \
    "Tier-IV data-centers with biometric access, redundant power/cooling, and DoD-cleared personnel." \
    "PE-2,PE-3,PE-13"

echo ""
echo "=== Done ==="
echo "Re-run any time — names are unique keys; existing rows are skipped."
echo "View at: http://localhost:5173/components (as CSP-Admin, NOT impersonating)"
