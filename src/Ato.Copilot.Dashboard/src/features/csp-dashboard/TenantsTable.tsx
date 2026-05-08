import { useEffect, useMemo, useState, type ReactElement } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  getCspDashboardTenants,
  isUnavailable,
  type ListTenantsParams,
  type TenantsPage,
  type TenantsSortField,
  type TenantStatus,
  type TenantSummary,
} from './api';
import { startImpersonation } from '../tenancy/api';

/**
 * Feature 048 / US8 / T184 — Cross-tenant tenants table.
 *
 * Paginated, sortable, filterable list of every tenant in the deployment.
 * Row click triggers `POST /api/tenants/{id}/impersonate` and navigates to
 * `/` (portfolio home) where `ImpersonationBanner` (T075) is already
 * mounted and per-route data refetches under the new tenant scope.
 *
 * Disabled tenants are present in the list (with a status badge) but every
 * KPI rollup column is reported as `0` per FR-098 — the server zeros them
 * out before this surface receives them.
 */
const SORT_FIELDS: { value: TenantsSortField; label: string }[] = [
  { value: 'displayName', label: 'Name' },
  { value: 'status', label: 'Status' },
  { value: 'openFindingCount', label: 'Open findings' },
  { value: 'lastActivityTimestamp', label: 'Last activity' },
];

const STATUS_FILTERS: { value: TenantStatus | ''; label: string }[] = [
  { value: '', label: 'All statuses' },
  { value: 'Active', label: 'Active' },
  { value: 'Suspended', label: 'Suspended' },
  { value: 'Disabled', label: 'Disabled' },
];

const STATUS_BADGE_CLASSES: Record<TenantStatus, string> = {
  Active: 'bg-emerald-100 text-emerald-800',
  Suspended: 'bg-amber-100 text-amber-800',
  Disabled: 'bg-slate-200 text-slate-700',
};

interface TenantsTableProps {
  /** Optional initial page size (defaults to 25, capped server-side at 200). */
  initialPageSize?: number;
}

export default function TenantsTable({
  initialPageSize = 25,
}: TenantsTableProps): ReactElement {
  const navigate = useNavigate();

  const [page, setPage] = useState(1);
  const [pageSize] = useState(initialPageSize);
  const [statusFilter, setStatusFilter] = useState<TenantStatus | ''>('');
  const [sort, setSort] = useState<TenantsSortField>('displayName');
  const [order, setOrder] = useState<'asc' | 'desc'>('asc');

  const [data, setData] = useState<TenantsPage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [busyTenantId, setBusyTenantId] = useState<string | null>(null);

  const params = useMemo<ListTenantsParams>(
    () => ({
      page,
      pageSize,
      status: statusFilter === '' ? undefined : statusFilter,
      sort,
      order,
    }),
    [page, pageSize, statusFilter, sort, order],
  );

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    getCspDashboardTenants(params)
      .then((result) => {
        if (cancelled) return;
        if (isUnavailable(result)) {
          // The page-level guard already redirected; just clear state.
          setData(null);
          return;
        }
        setData(result);
      })
      .catch((err: Error) => {
        if (cancelled) return;
        setError(err.message);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [params]);

  const handleSortToggle = (field: TenantsSortField) => {
    if (sort === field) {
      setOrder((o) => (o === 'asc' ? 'desc' : 'asc'));
    } else {
      setSort(field);
      setOrder('asc');
    }
    setPage(1);
  };

  const handleRowClick = async (tenant: TenantSummary) => {
    if (tenant.status === 'Disabled') {
      return;
    }
    setBusyTenantId(tenant.tenantId);
    try {
      await startImpersonation(tenant.tenantId, tenant.displayName);
      navigate('/');
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Impersonation failed.';
      setError(msg);
    } finally {
      setBusyTenantId(null);
    }
  };

  const totalPages = data
    ? Math.max(1, Math.ceil(data.totalCount / data.pageSize))
    : 1;

  return (
    <div
      className="rounded-lg border border-gray-200 bg-white shadow-sm"
      data-testid="csp-dashboard-tenants-table"
    >
      <div className="flex flex-wrap items-end justify-between gap-3 border-b border-gray-100 p-4">
        <div>
          <h3 className="text-sm font-semibold text-gray-700">Tenants</h3>
          <p className="text-xs text-gray-500">
            Click a row to impersonate the tenant and inspect its dashboards.
            Disabled tenants are listed but cannot be impersonated.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <label className="flex items-center gap-1 text-xs text-gray-600">
            Status
            <select
              value={statusFilter}
              onChange={(e) => {
                setStatusFilter(e.target.value as TenantStatus | '');
                setPage(1);
              }}
              className="rounded border border-gray-300 px-2 py-1 text-sm"
              data-testid="tenants-status-filter"
            >
              {STATUS_FILTERS.map((s) => (
                <option key={s.value} value={s.value}>
                  {s.label}
                </option>
              ))}
            </select>
          </label>
          <label className="flex items-center gap-1 text-xs text-gray-600">
            Sort by
            <select
              value={sort}
              onChange={(e) => {
                setSort(e.target.value as TenantsSortField);
                setPage(1);
              }}
              className="rounded border border-gray-300 px-2 py-1 text-sm"
              data-testid="tenants-sort-field"
            >
              {SORT_FIELDS.map((s) => (
                <option key={s.value} value={s.value}>
                  {s.label}
                </option>
              ))}
            </select>
          </label>
          <button
            type="button"
            onClick={() => setOrder((o) => (o === 'asc' ? 'desc' : 'asc'))}
            className="rounded border border-gray-300 px-2 py-1 text-xs text-gray-700 hover:bg-gray-50"
            data-testid="tenants-sort-order"
          >
            {order === 'asc' ? '↑ Asc' : '↓ Desc'}
          </button>
        </div>
      </div>

      {error && (
        <div
          className="border-b border-red-100 bg-red-50 px-4 py-2 text-sm text-red-700"
          role="alert"
          data-testid="tenants-error"
        >
          {error}
        </div>
      )}

      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-gray-200 text-sm">
          <thead className="bg-gray-50 text-xs font-medium uppercase tracking-wide text-gray-600">
            <tr>
              <SortableHeader
                label="Tenant"
                field="displayName"
                activeSort={sort}
                order={order}
                onClick={() => handleSortToggle('displayName')}
              />
              <SortableHeader
                label="Status"
                field="status"
                activeSort={sort}
                order={order}
                onClick={() => handleSortToggle('status')}
              />
              <th className="px-3 py-2 text-left">Onboarding</th>
              <th className="px-3 py-2 text-right">Orgs</th>
              <th className="px-3 py-2 text-right">Systems</th>
              <th className="px-3 py-2 text-right">ATOs (A/IP/D)</th>
              <SortableHeader
                label="Open findings"
                field="openFindingCount"
                activeSort={sort}
                order={order}
                onClick={() => handleSortToggle('openFindingCount')}
                align="right"
              />
              <th className="px-3 py-2 text-right">Open POA&amp;Ms</th>
              <th className="px-3 py-2 text-right">Open deviations</th>
              <SortableHeader
                label="Last activity"
                field="lastActivityTimestamp"
                activeSort={sort}
                order={order}
                onClick={() => handleSortToggle('lastActivityTimestamp')}
              />
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {loading && (
              <tr>
                <td
                  colSpan={10}
                  className="px-3 py-6 text-center text-sm text-gray-500"
                  data-testid="tenants-loading"
                >
                  Loading tenants…
                </td>
              </tr>
            )}
            {!loading && data && data.items.length === 0 && (
              <tr>
                <td
                  colSpan={10}
                  className="px-3 py-6 text-center text-sm text-gray-500"
                  data-testid="tenants-empty"
                >
                  No tenants match the current filter.
                </td>
              </tr>
            )}
            {!loading &&
              data?.items.map((t) => {
                const disabled = t.status === 'Disabled';
                const busy = busyTenantId === t.tenantId;
                return (
                  <tr
                    key={t.tenantId}
                    onClick={() => !disabled && !busy && handleRowClick(t)}
                    className={
                      disabled
                        ? 'bg-slate-50/40 text-slate-500'
                        : 'cursor-pointer hover:bg-indigo-50'
                    }
                    aria-disabled={disabled}
                    data-testid={`tenant-row-${t.tenantId}`}
                  >
                    <td className="whitespace-nowrap px-3 py-2 font-medium text-gray-900">
                      {t.displayName}
                      {busy && (
                        <span className="ml-2 text-xs text-indigo-600">
                          impersonating…
                        </span>
                      )}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2">
                      <span
                        className={`inline-flex items-center rounded px-2 py-0.5 text-xs font-medium ${STATUS_BADGE_CLASSES[t.status]}`}
                      >
                        {t.status}
                      </span>
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-xs text-gray-600">
                      {t.onboardingState}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {t.organizationCount.toLocaleString()}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {t.systemCount.toLocaleString()}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums text-xs">
                      {t.atoStatusCounts.authorized}/{t.atoStatusCounts.inProcess}/
                      {t.atoStatusCounts.denied}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {t.openFindingCount.toLocaleString()}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {t.openPoamCount.toLocaleString()}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-right tabular-nums">
                      {t.openDeviationCount.toLocaleString()}
                    </td>
                    <td className="whitespace-nowrap px-3 py-2 text-xs text-gray-500">
                      {t.lastActivityTimestamp
                        ? new Date(t.lastActivityTimestamp).toLocaleString()
                        : '—'}
                    </td>
                  </tr>
                );
              })}
          </tbody>
        </table>
      </div>

      {data && (
        <div
          className="flex items-center justify-between border-t border-gray-100 px-4 py-2 text-xs text-gray-600"
          data-testid="tenants-pagination"
        >
          <div>
            Page {data.page} of {totalPages} · {data.totalCount.toLocaleString()} tenants
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              disabled={data.page <= 1 || loading}
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              className="rounded border border-gray-300 px-2 py-1 disabled:opacity-50"
              data-testid="tenants-prev-page"
            >
              Previous
            </button>
            <button
              type="button"
              disabled={data.page >= totalPages || loading}
              onClick={() => setPage((p) => p + 1)}
              className="rounded border border-gray-300 px-2 py-1 disabled:opacity-50"
              data-testid="tenants-next-page"
            >
              Next
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

interface SortableHeaderProps {
  label: string;
  field: TenantsSortField;
  activeSort: TenantsSortField;
  order: 'asc' | 'desc';
  onClick: () => void;
  align?: 'left' | 'right';
}

function SortableHeader({
  label,
  field,
  activeSort,
  order,
  onClick,
  align = 'left',
}: SortableHeaderProps): ReactElement {
  const isActive = activeSort === field;
  const arrow = !isActive ? '' : order === 'asc' ? ' ↑' : ' ↓';
  return (
    <th
      onClick={onClick}
      className={`cursor-pointer select-none px-3 py-2 ${align === 'right' ? 'text-right' : 'text-left'} hover:text-gray-900`}
    >
      {label}
      {arrow}
    </th>
  );
}
