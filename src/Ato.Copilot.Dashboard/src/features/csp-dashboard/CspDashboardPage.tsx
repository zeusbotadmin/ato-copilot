import { useEffect, useState, type ReactElement } from 'react';
import { useNavigate } from 'react-router-dom';
import PageLayout from '../../components/layout/PageLayout';
import {
  getCspDashboardSummary,
  isUnavailable,
  type SummaryResponse,
  type UnavailableState,
} from './api';
import SummaryCards from './widgets/SummaryCards';
import AtoStatusChart from './widgets/AtoStatusChart';
import FindingsBySeverityChart from './widgets/FindingsBySeverityChart';
import TenantsTable from './TenantsTable';

/**
 * Feature 048 / US8 / T182 — CSP cross-tenant operational dashboard.
 *
 * Top-level page mounted at `/csp-dashboard`. Loads
 * `/api/csp/dashboard/summary` on mount and renders six KPI cards, two
 * charts, and the paginated tenants table. Drill-through happens inside
 * `TenantsTable` (row click → `POST /api/tenants/{id}/impersonate` →
 * navigate to `/`).
 *
 * Self-hides defensively when the deployment is `SingleTenant`, the
 * caller is not `CSP.Admin`, or CSP onboarding (US7) is not yet
 * `Active`. The page-level App route + sidebar gating already prevent
 * direct navigation in those cases; this is a defense-in-depth surface.
 */
type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; summary: SummaryResponse }
  | { kind: 'unavailable'; state: UnavailableState }
  | { kind: 'error'; message: string };

export default function CspDashboardPage(): ReactElement {
  const navigate = useNavigate();
  const [state, setState] = useState<LoadState>({ kind: 'loading' });

  useEffect(() => {
    let cancelled = false;
    setState({ kind: 'loading' });
    getCspDashboardSummary()
      .then((result) => {
        if (cancelled) return;
        if (isUnavailable(result)) {
          setState({ kind: 'unavailable', state: result });
          return;
        }
        setState({ kind: 'ready', summary: result });
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        const message = err instanceof Error ? err.message : 'Failed to load CSP dashboard.';
        setState({ kind: 'error', message });
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (state.kind === 'loading') {
    return (
      <PageLayout title="CSP Dashboard">
        <div className="text-sm text-gray-500" data-testid="csp-dashboard-loading">
          Loading CSP dashboard…
        </div>
      </PageLayout>
    );
  }

  if (state.kind === 'unavailable') {
    return (
      <PageLayout title="CSP Dashboard">
        <UnavailableSurface state={state.state} onHome={() => navigate('/')} />
      </PageLayout>
    );
  }

  if (state.kind === 'error') {
    return (
      <PageLayout title="CSP Dashboard">
        <div
          className="rounded border border-red-200 bg-red-50 p-4 text-sm text-red-700"
          role="alert"
          data-testid="csp-dashboard-error"
        >
          <div className="font-semibold">CSP dashboard unavailable</div>
          <div className="mt-1">{state.message}</div>
        </div>
      </PageLayout>
    );
  }

  const summary = state.summary;
  const showDisabledBanner = summary.disabledTenantCount > 0;

  return (
    <PageLayout title="CSP Dashboard">
      <div data-testid="csp-dashboard-page">
        <div className="mb-4 flex items-baseline justify-between">
          <div>
            <h1 className="text-2xl font-semibold text-gray-900">
              CSP cross-tenant dashboard
            </h1>
            <p className="text-sm text-gray-500">
              All-up KPIs across every tenant in the deployment.
            </p>
          </div>
          <div className="text-xs text-gray-500" data-testid="csp-dashboard-generated-at">
            Generated {new Date(summary.generatedAt).toLocaleString()}
          </div>
        </div>

        {showDisabledBanner && (
          <div
            className="mb-4 rounded border border-slate-200 bg-slate-50 px-4 py-2 text-sm text-slate-700"
            role="status"
            data-testid="csp-dashboard-disabled-banner"
          >
            {summary.disabledTenantCount.toLocaleString()} disabled tenant
            {summary.disabledTenantCount === 1 ? '' : 's'} excluded from rollups
            below.
          </div>
        )}

        <SummaryCards summary={summary} />

        <div className="mt-6 grid grid-cols-1 gap-4 lg:grid-cols-2">
          <AtoStatusChart counts={summary.atoStatusCounts} />
          <FindingsBySeverityChart
            counts={summary.openFindingsBySeverity}
            openPoamCount={summary.openPoamCount}
            openDeviationCount={summary.openDeviationCount}
          />
        </div>

        <div className="mt-6">
          <TenantsTable />
        </div>
      </div>
    </PageLayout>
  );
}

interface UnavailableSurfaceProps {
  state: UnavailableState;
  onHome: () => void;
}

function UnavailableSurface({
  state,
  onHome,
}: UnavailableSurfaceProps): ReactElement {
  const message =
    state.reason === 'SINGLE_TENANT_MODE'
      ? 'This deployment runs in SingleTenant mode; the cross-tenant CSP dashboard is not applicable.'
      : state.reason === 'NOT_CSP_ADMIN'
        ? 'You do not have CSP.Admin access. The cross-tenant dashboard is restricted to CSP administrators.'
        : state.reason === 'CSP_ONBOARDING_INCOMPLETE'
          ? 'Complete the CSP onboarding wizard before opening the cross-tenant dashboard.'
          : 'The CSP dashboard service is unreachable. Try again in a moment.';
  return (
    <div
      className="rounded border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800"
      role="alert"
      data-testid={`csp-dashboard-unavailable-${state.reason}`}
    >
      <div className="font-semibold">CSP dashboard unavailable</div>
      <div className="mt-1">{message}</div>
      <button
        type="button"
        onClick={onHome}
        className="mt-3 rounded border border-amber-300 px-3 py-1 text-xs font-medium text-amber-900 hover:bg-amber-100"
      >
        Return to portfolio
      </button>
    </div>
  );
}
