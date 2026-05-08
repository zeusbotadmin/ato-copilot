import { useEffect, useState } from 'react';
import { getCspDashboardSummary, isUnavailable } from '../../features/csp-dashboard/api';

/**
 * Feature 048 / US8 / T186 — visibility probe for the top-level
 * `/csp-dashboard` nav link. Performs a single `GET /api/csp/dashboard/summary`
 * probe on mount.
 *
 *   - 200 → caller is `CSP.Admin`, deployment is `MultiTenant`, and CSP
 *     onboarding is `Active`. The link is shown.
 *   - 404 (`SINGLE_TENANT_MODE`) / 401|403 (`NOT_CSP_ADMIN`) /
 *     503 (`CSP_ONBOARDING_INCOMPLETE`) / network unreachable → link hidden.
 *
 * Returns `null` while the probe is in flight so the link does not flash
 * during initial page paint.
 */
export function useCspDashboardAvailable(): boolean | null {
  const [available, setAvailable] = useState<boolean | null>(null);

  useEffect(() => {
    let cancelled = false;
    getCspDashboardSummary()
      .then((result) => {
        if (cancelled) return;
        setAvailable(!isUnavailable(result));
      })
      .catch(() => {
        if (!cancelled) setAvailable(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  return available;
}
