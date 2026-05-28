#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────
#  Seed five representative test systems via the Dashboard API.
#  Idempotent — each system is created only if a system with the
#  same name does not already exist. Prints UUID for each system.
#
#  Usage:
#    ./scripts/seed-systems.sh
#
#  Honours the same env conventions as seed-dashboard.sh:
#    ATO_BASE_URL  (full base, default http://localhost:${ATO_SERVER_PORT:-3001}/api/dashboard)
#    ATO_SERVER_PORT (just the port; .env is auto-loaded if neither is set)
# ──────────────────────────────────────────────────────────────

set -euo pipefail

if [ -z "${ATO_BASE_URL:-}" ]; then
  if [ -z "${ATO_SERVER_PORT:-}" ] && [ -f "$(dirname "$0")/../.env" ]; then
    set -o allexport; . "$(dirname "$0")/../.env"; set +o allexport || true
  fi
  ATO_BASE_URL="http://localhost:${ATO_SERVER_PORT:-3001}/api/dashboard"
fi
BASE="$ATO_BASE_URL"
H="Content-Type: application/json"

# CSP onboarding base (Feature 048 US7).
CSP_BASE="${ATO_BASE_URL%/api/dashboard}/api/csp/onboarding"

echo "Seeding test systems via $BASE"

echo ""
echo "=== Ensuring CSP Profile (PEO-790 / flankspeed) ==="

# Idempotent CSP profile seed. Non-destructive by design:
# - If no/partial profile exists, complete wizard with the seed values.
# - If an ACTIVE profile exists with different values, keep it and report.
#   (prevents clobbering manually-entered environment data)
csp_state_json=$(curl -fsS "$CSP_BASE/state")
csp_onboarding_state=$(echo "$csp_state_json" | python3 -c "import sys,json; d=json.load(sys.stdin); print((d.get('data') or {}).get('onboardingState') or '')")
csp_display_name=$(echo "$csp_state_json" | python3 -c "import sys,json; d=json.load(sys.stdin); print((((d.get('data') or {}).get('identity') or {}).get('displayName')) or '')")
csp_legal_name=$(echo "$csp_state_json" | python3 -c "import sys,json; d=json.load(sys.stdin); print((((d.get('data') or {}).get('identity') or {}).get('legalEntityName')) or '')")

if [ "$csp_onboarding_state" = "Active" ]; then
  if [ "$csp_display_name" = "flankspeed" ] && [ "$csp_legal_name" = "PEO-790" ]; then
    echo "  CSP profile already active: PEO-790 / flankspeed"
  else
    echo "  CSP profile already active with custom values: $csp_legal_name / $csp_display_name"
    echo "  Skipping CSP overwrite to preserve existing environment data."
  fi
else
  echo "  Completing CSP onboarding wizard with PEO-790 / flankspeed..."

  csp_identity=$(curl -fsS -X POST "$CSP_BASE/identity" -H "$H" -d '{"legalEntityName":"PEO-790","displayName":"flankspeed","logoUrl":null}')
  csp_support=$(curl -fsS -X POST "$CSP_BASE/support" -H "$H" -d '{"primarySupportEmail":"roger.potts@mil.mil","supportPhone":null}')
  csp_classification=$(curl -fsS -X POST "$CSP_BASE/classification" -H "$H" -d '{"defaultClassificationFloor":"CUI"}')
  csp_submit=$(curl -fsS -X POST "$CSP_BASE/submit" -H "$H")

  submit_status=$(echo "$csp_submit" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('status',''))")
  if [ "$submit_status" != "success" ]; then
    echo "ERROR: CSP submit failed: $csp_submit" >&2
    exit 1
  fi

  echo "  CSP profile seeded: PEO-790 / flankspeed"
fi

# ──────────────────────────────────────────────────────────────
#  Seed demo organizations (tenants hosted under the CSP).
#  Feature 048 US7 — only runs in MultiTenant deployments where
#  the caller is a CSP.Admin. The CSP dashboard's "Orgs" table
#  lists rows from db.Tenants minus the system/default vestige
#  tenants, so without this phase the CSP dashboard is empty
#  even after the systems seed.
#
#  Configurable via CSP_ORGS — semicolon-separated entries of
#  the form "displayName[:legalEntityName]". Default mirrors the
#  flankspeed demo (PEO-790 plus two mission-owner sub-orgs).
# ──────────────────────────────────────────────────────────────
CSP_DASH_BASE="${ATO_BASE_URL%/api/dashboard}/api/csp/dashboard"

echo ""
echo "=== Ensuring demo orgs (tenants under the CSP) ==="

csp_probe_code=$(curl -s -o /dev/null -w "%{http_code}" "$CSP_DASH_BASE/summary" || true)
if [ "$csp_probe_code" != "200" ]; then
  echo "  CSP dashboard surface not available (HTTP $csp_probe_code). Skipping org seed."
  echo "  Hint: only runs in MultiTenant mode for a CSP.Admin caller."
else
  ORGS_SPEC="${CSP_ORGS:-PEO-790;PMA 290:PMA-290;PMS 408:PMS-408}"

  # Lower-cased displayName set of existing tenants, for case-insensitive
  # dedupe (matches the service-side guard in CspDashboardService.CreateTenantAsync).
  existing_names=$(curl -fsS "$CSP_DASH_BASE/tenants?pageSize=200" 2>/dev/null \
    | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
    items = (d.get('data') or {}).get('items') or []
    print('\n'.join(((i.get('displayName') or '').strip().lower()) for i in items))
except Exception:
    pass
" || true)

  org_have() {
    local needle
    needle=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
    printf '%s\n' "$existing_names" | grep -Fxq -- "$needle"
  }

  IFS=';' read -ra ORG_ITEMS <<< "$ORGS_SPEC"
  for raw in "${ORG_ITEMS[@]}"; do
    spec=$(printf '%s' "$raw" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
    [ -z "$spec" ] && continue
    display="${spec%%:*}"
    legal=""
    if [[ "$spec" == *:* ]]; then
      legal="${spec#*:}"
    fi
    display=$(printf '%s' "$display" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
    legal=$(printf '%s' "$legal" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')

    if org_have "$display"; then
      echo "  Org already exists: $display"
      continue
    fi

    body=$(python3 -c "
import json, sys
display, legal = sys.argv[1], sys.argv[2]
out = {'displayName': display}
if legal:
    out['legalEntityName'] = legal
print(json.dumps(out))
" "$display" "$legal")

    resp=$(curl -sS -X POST "$CSP_DASH_BASE/tenants" -H "$H" -d "$body")
    new_id=$(printf '%s' "$resp" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
    print(((d.get('data') or {}).get('tenantId')) or '')
except Exception:
    print('')
" || true)
    if [ -n "$new_id" ]; then
      if [ -n "$legal" ]; then
        echo "  Created org: $display ($legal) -> $new_id"
      else
        echo "  Created org: $display -> $new_id"
      fi
    else
      echo "  WARN: failed to create org '$display': $resp" >&2
    fi
  done

  # ────────────────────────────────────────────────────────────
  #  Pre-complete the per-org onboarding wizard (Feature 047).
  #  Without this phase, OnboardingGate force-opens its modal
  #  the first time a CSP-Admin impersonates a freshly-seeded
  #  org because `OrganizationContext` + `Roles` are unset, and
  #  the dashboard never renders. This is dev-seed only — the
  #  gate continues to enforce the two required steps for any
  #  real org created post-seed.
  #
  #  Two-phase flow:
  #    1) For each seeded org whose tenant is NOT yet Active,
  #       impersonate it and walk through the Feature 048 tenant
  #       wizard (legal-entity → submit). Tenant-wizard endpoints
  #       use `ITenantContext.EffectiveTenantId` and therefore
  #       honor the `ato-impersonate` cookie correctly.
  #
  #    2) Once tenants are Active, pre-complete the Feature 047
  #       org-wizard *once* against the simulated CSP-Admin's
  #       home tenant — no impersonation. The org-wizard
  #       endpoints (OrganizationContextEndpoints,
  #       RoleAssignmentEndpoints, OnboardingStateEndpoints) read
  #       the tenant id from `ClaimsPrincipal.tid` rather than
  #       `ITenantContext`, so they always target the caller's
  #       home tenant regardless of impersonation. That's a real
  #       Feature 047/048 alignment gap — flagged here for a
  #       follow-up — but for dev seeding it works in our favor:
  #       a single completion satisfies the gate for every
  #       subsequent impersonation visit.
  #
  #  The `ato-impersonate` cookie is issued with `Secure; HttpOnly`,
  #  so we manually extract it from the response and re-pass via
  #  `-H "Cookie: …"` (curl's jar refuses Secure cookies on http://).
  # ────────────────────────────────────────────────────────────
  echo ""
  echo "=== Pre-completing org-onboarding wizards (Feature 047 gate bypass) ==="

  API_ROOT="${ATO_BASE_URL%/api/dashboard}/api"
  ONB_BASE="$API_ROOT/onboarding"

  # POST /tenants/{tid}/impersonate → echo the cookie value
  # (form: `ato-impersonate=<value>`), or empty on failure.
  impersonate_cookie() {
    local tid="$1"
    local headers
    headers=$(curl -sS -i -X POST "$API_ROOT/tenants/$tid/impersonate" -H "$H" 2>/dev/null || true)
    # BSD awk (macOS) has no `IGNORECASE`; rely on the explicit `[Ss]et-[Cc]ookie:`
    # character class for portability across BSD/GNU awk.
    printf '%s\n' "$headers" \
      | awk '/^[Ss]et-[Cc]ookie:/ {
               line=$0
               sub(/^[Ss]et-[Cc]ookie:[ \t]*/, "", line)
               n=split(line, parts, ";")
               if (parts[1] ~ /^ato-impersonate=/) { print parts[1]; exit }
             }'
  }

  # GET /onboarding/state with the given Cookie header — echoes `true` when
  # both required steps (OrganizationContext + Roles) are already Completed.
  org_onboarding_complete() {
    local cookie="$1"
    local resp
    resp=$(curl -sS "$ONB_BASE/state" -H "$H" -H "Cookie: $cookie" 2>/dev/null || echo "{}")
    printf '%s\n' "$resp" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
    steps = (d.get('data') or {}).get('steps') or []
    have_oc    = any(s.get('step')=='OrganizationContext' and s.get('status')=='Completed' for s in steps)
    have_roles = any(s.get('step')=='Roles'               and s.get('status')=='Completed' for s in steps)
    print('true' if (have_oc and have_roles) else 'false')
except Exception:
    print('false')
"
  }

  # Default branch per display name. PEO-790 / PMA / PMS are all Navy
  # (matches the flankspeed demo profile).
  display_to_branch() {
    case "$1" in
      PEO-790|PMA*|PMS*) echo "Navy" ;;
      *)                 echo "Navy" ;;
    esac
  }

  # Refresh tenant listing once so we can resolve display name → tenantId.
  # (The CSP dashboard surface uses `tenantId` as the row identifier — see
  # `CspDashboardEndpoints.cs` / `CspTenantSummaryDto`.)
  tenants_listing=$(curl -fsS "$CSP_DASH_BASE/tenants?pageSize=200" 2>/dev/null || echo "{}")

  # Resolve a display name (case-insensitive) to its tenant id, or empty.
  resolve_tenant_id() {
    printf '%s\n' "$tenants_listing" | python3 -c "
import sys, json
target = sys.argv[1].strip().lower()
try:
    d = json.load(sys.stdin)
    for it in ((d.get('data') or {}).get('items') or []):
        if (it.get('displayName') or '').strip().lower() == target:
            print(it.get('tenantId') or it.get('id') or '')
            break
    else:
        print('')
except Exception:
    print('')
" "$1"
  }

  # Tenant-level onboarding state (Active / InWizard / Pending) for a given
  # display name. Used to decide whether to run the Feature 048 tenant wizard
  # before pre-completing the Feature 047 org wizard.
  resolve_tenant_state() {
    printf '%s\n' "$tenants_listing" | python3 -c "
import sys, json
target = sys.argv[1].strip().lower()
try:
    d = json.load(sys.stdin)
    for it in ((d.get('data') or {}).get('items') or []):
        if (it.get('displayName') or '').strip().lower() == target:
            print(it.get('onboardingState') or '')
            break
    else:
        print('')
except Exception:
    print('')
" "$1"
  }

  # Walk the Feature 048 tenant wizard (7 steps + submit) while already
  # impersonating the tenant. Idempotent — no-ops once the tenant is Active.
  # Returns 0 on success, non-zero on failure.
  submit_tenant_wizard() {
    local display="$1" legal="$2" cookie="$3"
    local branch defclass legal_name body resp
    branch=$(display_to_branch "$display")
    defclass="CUI"
    legal_name="${legal:-$display}"

    # Probe current state — bail early if already Active.
    resp=$(curl -sS "$ONB_BASE/tenant/state" -H "$H" -H "Cookie: $cookie" 2>/dev/null || echo "{}")
    local current_state
    current_state=$(printf '%s' "$resp" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
    print(((d.get('data') or {}).get('onboardingState')) or '')
except Exception:
    print('')
" 2>/dev/null || true)
    if [ "$current_state" = "Active" ]; then
      return 0
    fi

    # Step 1 — legal-entity.
    body=$(python3 -c "
import json, sys
print(json.dumps({'legalEntityName': sys.argv[1], 'doDComponent': sys.argv[2], 'timeZone': 'America/New_York'}))
" "$legal_name" "Navy")
    curl -sS -X POST "$ONB_BASE/tenant/legal-entity" -H "$H" -H "Cookie: $cookie" -d "$body" >/dev/null 2>&1 || true

    # Step 2 — HQ address (placeholder — seed only).
    body='{"hqAddressLine1":"700 N Brand Blvd","hqCity":"Arlington","hqStateOrProvince":"VA","hqPostalCode":"22202","hqCountry":"USA"}'
    curl -sS -X POST "$ONB_BASE/tenant/hq-address" -H "$H" -H "Cookie: $cookie" -d "$body" >/dev/null 2>&1 || true

    # Step 3 — default classification.
    body=$(python3 -c "import json,sys; print(json.dumps({'defaultClassificationLevel': sys.argv[1]}))" "$defclass")
    curl -sS -X POST "$ONB_BASE/tenant/classification" -H "$H" -H "Cookie: $cookie" -d "$body" >/dev/null 2>&1 || true

    # Step 4 — Authorizing Official (placeholder POC).
    body='{"authorizingOfficialName":"Demo AO","authorizingOfficialEmail":"demo-ao@mil.mil"}'
    curl -sS -X POST "$ONB_BASE/tenant/ao" -H "$H" -H "Cookie: $cookie" -d "$body" >/dev/null 2>&1 || true

    # Step 5 — primary POC.
    body='{"primaryPocName":"Roger Potts","primaryPocEmail":"roger.potts@mil.mil","primaryPocPhone":null}'
    curl -sS -X POST "$ONB_BASE/tenant/primary-poc" -H "$H" -H "Cookie: $cookie" -d "$body" >/dev/null 2>&1 || true

    # Step 6 — first organization profile (created at submit time).
    body=$(python3 -c "
import json, sys
print(json.dumps({'name': sys.argv[1], 'description': 'Seeded by scripts/seed-systems.sh'}))
" "$display")
    curl -sS -X POST "$ONB_BASE/tenant/org-profile" -H "$H" -H "Cookie: $cookie" -d "$body" >/dev/null 2>&1 || true

    # Step 7 — final submit. This is the operation that flips the tenant to
    # Active and invalidates the tenant-resolution cache (the fix from the
    # earlier session — see TenantResolutionCacheKeys + SubmitFinalAsync).
    resp=$(curl -sS -X POST "$ONB_BASE/tenant/submit" -H "$H" -H "Cookie: $cookie" 2>/dev/null || echo "{}")
    local submit_state
    submit_state=$(printf '%s' "$resp" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
    print(((d.get('data') or {}).get('onboardingState')) or '')
except Exception:
    print('')
" 2>/dev/null || true)
    if [ "$submit_state" != "Active" ]; then
      echo "  WARN: tenant-wizard submit did not activate '$display' (state='$submit_state'): $resp" >&2
      return 1
    fi
    return 0
  }

  complete_org_wizard() {
    local display="$1" legal="$2" tid="$3"

    local cookie
    cookie=$(impersonate_cookie "$tid")
    if [ -z "$cookie" ]; then
      echo "  WARN: could not impersonate '$display' ($tid); skipping tenant-wizard pre-seed." >&2
      return
    fi

    # Feature 048 tenant wizard MUST be Active before any per-tenant
    # operation works — `/api/*` is not in the tenant-resolution allowlist
    # and 403s with `TENANT_ONBOARDING_INCOMPLETE` until the tenant submits.
    local tstate
    tstate=$(resolve_tenant_state "$display")
    if [ "$tstate" != "Active" ]; then
      echo "  Submitting tenant wizard for '$display' (state was '$tstate')..."
      if submit_tenant_wizard "$display" "$legal" "$cookie"; then
        echo "    -> '$display' tenant is now Active."
      fi
    else
      echo "  Tenant '$display' already Active."
    fi

    curl -sS -X DELETE "$API_ROOT/tenants/impersonation" -H "$H" -H "Cookie: $cookie" >/dev/null 2>&1 || true
  }

  # Pre-complete the Feature 047 org-wizard against the simulated CSP-Admin's
  # HOME tenant (the dev `CacAuth.SimulatedIdentity.TenantId`). Feature 047
  # endpoints read the tenant id from `ClaimsPrincipal.tid` rather than
  # `ITenantContext`, so they always target the caller's home tenant
  # regardless of impersonation. Completing the wizard once at the home-tenant
  # scope is therefore sufficient to silence OnboardingGate for every
  # subsequent impersonation visit.
  complete_csp_home_org_wizard() {
    local state_resp have_oc have_roles
    state_resp=$(curl -sS "$ONB_BASE/state" -H "$H" 2>/dev/null || echo "{}")
    have_oc=$(printf '%s' "$state_resp" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
    steps = (d.get('data') or {}).get('steps') or []
    print('true' if any(s.get('step')=='OrganizationContext' and s.get('status')=='Completed' for s in steps) else 'false')
except Exception:
    print('false')
" 2>/dev/null || echo "false")
    have_roles=$(printf '%s' "$state_resp" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
    steps = (d.get('data') or {}).get('steps') or []
    print('true' if any(s.get('step')=='Roles' and s.get('status')=='Completed' for s in steps) else 'false')
except Exception:
    print('false')
" 2>/dev/null || echo "false")

    if [ "$have_oc" = "true" ] && [ "$have_roles" = "true" ]; then
      echo "  CSP-home org-wizard already complete; OnboardingGate stays inert."
      return
    fi

    # Step 0 — start the wizard. This is the only path that atomically grants
    # the calling subject the bootstrap Administrator RmfRole (see
    # OnboardingStateService.StartAsync → IBootstrapAdministratorService.GrantAsync).
    # Without this grant, the subsequent POSTs to /role-assignments fail with
    # `RBAC_ROLE_ASSIGN_DENIED` ("Caller holds no RmfRole-bearing assignment
    # for this tenant"). Idempotent — `GrantAsync` no-ops if an admin already
    # exists, and `StartAsync` is safe to call when the state is already
    # InProgress.
    curl -sS -X POST "$ONB_BASE/start" -H "$H" >/dev/null 2>&1 || true

    # Step 1 — Organization Context (auto-marks `OrganizationContext` step Completed).
    local oc_body oc_resp
    oc_body='{"organizationName":"PEO-790","branch":"Navy","classificationPosture":"CUI","primaryPocEmail":"roger.potts@mil.mil"}'
    oc_resp=$(curl -sS -X PUT "$ONB_BASE/organization-context" -H "$H" -d "$oc_body" 2>/dev/null || true)
    if ! printf '%s' "$oc_resp" | python3 -c "import sys,json; sys.exit(0 if json.load(sys.stdin).get('ok') else 1)" 2>/dev/null; then
      echo "  WARN: organization-context PUT failed: $oc_resp" >&2
      return
    fi
    echo "  OrganizationContext marked Completed (CSP home tenant)."

    # Step 2 — Create 3 demo persons + 3 role assignments (ISSM/ISSO/Administrator).
    # Roles step is auto-marked Completed only when all three roles have a holder.
    for role in Issm Isso Administrator; do
      local lcrole person_email person_name person_body pcreate pid
      lcrole=$(printf '%s' "$role" | tr '[:upper:]' '[:lower:]')
      person_email="seed-${lcrole}@mil.mil"
      person_name="Demo ${role}"

      person_body=$(python3 -c "
import json, sys
print(json.dumps({'displayName': sys.argv[1], 'email': sys.argv[2]}))
" "$person_name" "$person_email")

      pcreate=$(curl -sS -X POST "$ONB_BASE/persons" -H "$H" -d "$person_body" 2>/dev/null || true)
      pid=$(printf '%s' "$pcreate" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
    print(((d.get('data') or {}).get('id')) or '')
except Exception:
    print('')
" 2>/dev/null || true)

      # Fall back to a directory lookup if creation failed (re-run idempotency).
      if [ -z "$pid" ]; then
        local qenc plist
        qenc=$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1]))" "$person_email")
        plist=$(curl -sS "$ONB_BASE/persons?query=$qenc" -H "$H" 2>/dev/null || echo "{}")
        pid=$(printf '%s' "$plist" | python3 -c "
import sys, json
target = sys.argv[1].lower()
try:
    d = json.load(sys.stdin)
    for it in (d.get('data') or []):
        if (it.get('email') or '').lower() == target:
            print(it.get('id') or '')
            break
    else:
        print('')
except Exception:
    print('')
" "$person_email" 2>/dev/null || true)
      fi

      if [ -z "$pid" ]; then
        echo "  WARN: could not create/find demo person for role $role." >&2
        continue
      fi

      local ra_body
      ra_body=$(python3 -c "import json,sys; print(json.dumps({'role': sys.argv[1], 'personId': sys.argv[2]}))" "$role" "$pid")
      curl -sS -X POST "$ONB_BASE/role-assignments" -H "$H" -d "$ra_body" >/dev/null 2>&1 || true
    done

    # Verify.
    state_resp=$(curl -sS "$ONB_BASE/state" -H "$H" 2>/dev/null || echo "{}")
    local final_ok
    final_ok=$(printf '%s' "$state_resp" | python3 -c "
import sys, json
try:
    d = json.load(sys.stdin)
    steps = (d.get('data') or {}).get('steps') or []
    have_oc    = any(s.get('step')=='OrganizationContext' and s.get('status')=='Completed' for s in steps)
    have_roles = any(s.get('step')=='Roles'               and s.get('status')=='Completed' for s in steps)
    print('true' if (have_oc and have_roles) else 'false')
except Exception:
    print('false')
" 2>/dev/null || echo "false")

    if [ "$final_ok" = "true" ]; then
      echo "  Roles marked Completed. OnboardingGate will stay inert from now on."
    else
      echo "  WARN: org-wizard pre-completion did not satisfy both required steps." >&2
    fi
  }

  # Phase 1 — per-org tenant wizard (Feature 048).
  for raw in "${ORG_ITEMS[@]}"; do
    spec=$(printf '%s' "$raw" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
    [ -z "$spec" ] && continue
    display="${spec%%:*}"
    legal=""
    if [[ "$spec" == *:* ]]; then
      legal="${spec#*:}"
    fi
    display=$(printf '%s' "$display" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
    legal=$(printf '%s' "$legal" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')

    tid=$(resolve_tenant_id "$display")
    if [ -z "$tid" ]; then
      echo "  WARN: could not resolve tenant id for '$display'; skipping wizard pre-seed." >&2
      continue
    fi
    complete_org_wizard "$display" "$legal" "$tid"
  done

  # Phase 2 — CSP-home org-wizard (Feature 047). One-shot, no impersonation.
  echo ""
  echo "  --- Completing org-wizard at CSP-home scope (Feature 047 gate) ---"
  complete_csp_home_org_wizard

  echo ""
  echo "  Seeded orgs are wizard-pre-completed: PMA 290 / PMS 408 tenant"
  echo "  wizards are submitted (OnboardingState=Active in the CSP listing),"
  echo "  and OrganizationContext + Roles are recorded at CSP-home scope so"
  echo "  the CSP-Admin lands directly on the impersonated org's dashboard."
  echo "  Remaining optional wizard steps (eMASS, SSP PDF, Subscriptions,"
  echo "  Templates, NarrativeSeeds) can still be run from /onboarding."
fi

# Find an existing system by exact name; echo its id, or empty string.
find_system_id() {
  local name="$1"
  local encoded
  encoded=$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1]))" "$name")
  curl -fsS "$BASE/portfolio?search=$encoded&pageSize=50" 2>/dev/null \
    | python3 -c "
import sys, json
target = sys.argv[1]
try:
    data = json.load(sys.stdin)
except Exception:
    print('')
    sys.exit(0)
for it in data.get('items', []):
    if it.get('name') == target:
        # Portfolio DTO uses 'systemId'; some legacy paths return 'id'
        print(it.get('systemId') or it.get('id') or '')
        sys.exit(0)
print('')
" "$name"
}

# Create-or-find. Echoes the system UUID.
upsert_system() {
  local name="$1"
  local body="$2"
  local existing
  existing=$(find_system_id "$name")
  if [ -n "$existing" ]; then
    echo "$existing"
    return
  fi
  local resp
  resp=$(curl -sS -X POST "$BASE/systems" -H "$H" -d "$body")
  local id
  id=$(echo "$resp" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('id',''))" 2>/dev/null || echo "")
  if [ -z "$id" ]; then
    echo "ERROR creating '$name': $resp" >&2
    return 1
  fi
  echo "$id"
}

echo ""
echo "=== Registering 5 Test Systems (idempotent) ==="

# 1) Eagle Eye — flagship test system referenced by seed-dashboard.sh
EAGLE_EYE=$(upsert_system "Eagle Eye" '{
  "name": "Eagle Eye",
  "acronym": "EAGLE-EYE",
  "systemType": "MajorApplication",
  "missionCriticality": "MissionCritical",
  "hostingEnvironment": "AzureGovernment",
  "description": "Flagship intelligence-surveillance-reconnaissance (ISR) data fusion platform. NIST 800-53 Moderate baseline.",
  "cloudEnvironment": "Government",
  "subscriptionIds": []
}')
echo "  Eagle Eye        : $EAGLE_EYE"

# 2) Eagle Nest — companion enclave
EAGLE_NEST=$(upsert_system "Eagle Nest" '{
  "name": "Eagle Nest",
  "acronym": "EAGLE-NEST",
  "systemType": "Enclave",
  "missionCriticality": "MissionEssential",
  "hostingEnvironment": "AzureGovernment",
  "description": "Operations enclave hosting analytics back-end services for the Eagle Eye platform.",
  "cloudEnvironment": "Government",
  "subscriptionIds": []
}')
echo "  Eagle Nest       : $EAGLE_NEST"

# 3) Phoenix Falcon — second mission-essential MajorApplication
PHOENIX_FALCON=$(upsert_system "Phoenix Falcon" '{
  "name": "Phoenix Falcon",
  "acronym": "PHX-FALCON",
  "systemType": "MajorApplication",
  "missionCriticality": "MissionEssential",
  "hostingEnvironment": "AzureGovernment",
  "description": "Logistics & supply-chain decision-support application. NIST 800-53 Moderate baseline, FedRAMP High overlay.",
  "cloudEnvironment": "Government",
  "subscriptionIds": []
}')
echo "  Phoenix Falcon   : $PHOENIX_FALCON"

# 4) Coastal Watch — admin / mission-support enclave
COASTAL_WATCH=$(upsert_system "Coastal Watch" '{
  "name": "Coastal Watch",
  "acronym": "COASTAL",
  "systemType": "Enclave",
  "missionCriticality": "MissionSupport",
  "hostingEnvironment": "AzureGovernment",
  "description": "Maritime domain awareness collaboration enclave. NIST 800-53 Low baseline.",
  "cloudEnvironment": "Government",
  "subscriptionIds": []
}')
echo "  Coastal Watch    : $COASTAL_WATCH"

# 5) Polar Bear — air-gapped IL6 PlatformIT
POLAR_BEAR=$(upsert_system "Polar Bear" '{
  "name": "Polar Bear",
  "acronym": "POLAR",
  "systemType": "PlatformIt",
  "missionCriticality": "MissionCritical",
  "hostingEnvironment": "AzureGovernmentAirGapped",
  "description": "Air-gapped IL6 platform service providing classified directory and DNS. NIST 800-53 High baseline + CNSSI 1253 overlay.",
  "cloudEnvironment": "GovernmentAirGappedIl6",
  "subscriptionIds": []
}')
echo "  Polar Bear       : $POLAR_BEAR"

echo ""
echo "=== Summary ==="
curl -fsS "$BASE/portfolio?pageSize=50" | python3 -c "
import sys, json
data = json.load(sys.stdin)
print(f\"  Total systems: {data.get('totalCount', 0)}\")
for it in sorted(data.get('items', []), key=lambda x: x.get('name','')):
    print(f\"    - {it.get('name'):16s}  type={it.get('systemType'):16s}  criticality={it.get('missionCriticality'):16s}  phase={it.get('currentRmfPhase')}\")
"

echo ""
echo "Done. To wire components & control mappings to these systems, run:"
echo "    ./scripts/seed-dashboard.sh"
