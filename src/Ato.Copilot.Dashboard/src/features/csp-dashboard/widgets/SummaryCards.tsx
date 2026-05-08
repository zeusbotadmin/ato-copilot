import type { ReactElement } from 'react';
import type { SummaryResponse } from '../api';

/**
 * Feature 048 / US8 / T183 — KPI summary cards for the CSP cross-tenant
 * dashboard. Renders six headline tiles arranged on a responsive grid:
 *
 *   1. Active tenants
 *   2. Suspended tenants
 *   3. Disabled tenants  (excluded from rollups; FR-098)
 *   4. Total organizations
 *   5. Total systems
 *   6. Total ATO decisions
 *
 * Disabled-tenant exclusion is signaled with an explanatory caption on
 * the disabled-tenants tile so the user understands why downstream rollups
 * may be lower than `tenantCounts.total`.
 */
export interface SummaryCardsProps {
  summary: SummaryResponse;
}

interface CardSpec {
  label: string;
  value: number;
  caption?: string;
  accent: 'emerald' | 'amber' | 'slate' | 'indigo' | 'sky' | 'violet';
  testId: string;
}

const ACCENT_CLASSES: Record<CardSpec['accent'], string> = {
  emerald: 'border-emerald-200 bg-emerald-50',
  amber: 'border-amber-200 bg-amber-50',
  slate: 'border-slate-200 bg-slate-50',
  indigo: 'border-indigo-200 bg-indigo-50',
  sky: 'border-sky-200 bg-sky-50',
  violet: 'border-violet-200 bg-violet-50',
};

const VALUE_ACCENT_CLASSES: Record<CardSpec['accent'], string> = {
  emerald: 'text-emerald-700',
  amber: 'text-amber-700',
  slate: 'text-slate-700',
  indigo: 'text-indigo-700',
  sky: 'text-sky-700',
  violet: 'text-violet-700',
};

export default function SummaryCards({ summary }: SummaryCardsProps): ReactElement {
  const ato = summary.atoStatusCounts;
  const totalAtos = ato.authorized + ato.inProcess + ato.denied;

  const cards: CardSpec[] = [
    {
      label: 'Active tenants',
      value: summary.tenantCounts.active,
      accent: 'emerald',
      testId: 'kpi-active-tenants',
    },
    {
      label: 'Suspended tenants',
      value: summary.tenantCounts.suspended,
      accent: 'amber',
      testId: 'kpi-suspended-tenants',
    },
    {
      label: 'Disabled tenants',
      value: summary.disabledTenantCount,
      caption: 'Excluded from rollups below',
      accent: 'slate',
      testId: 'kpi-disabled-tenants',
    },
    {
      label: 'Total organizations',
      value: summary.organizationCount,
      accent: 'indigo',
      testId: 'kpi-organizations',
    },
    {
      label: 'Total systems',
      value: summary.systemCount,
      accent: 'sky',
      testId: 'kpi-systems',
    },
    {
      label: 'Total ATO decisions',
      value: totalAtos,
      caption: `${ato.authorized} authorized · ${ato.inProcess} in process · ${ato.denied} denied`,
      accent: 'violet',
      testId: 'kpi-atos',
    },
  ];

  return (
    <div
      className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6"
      data-testid="csp-dashboard-summary-cards"
    >
      {cards.map((c) => (
        <div
          key={c.testId}
          className={`rounded-lg border p-4 shadow-sm ${ACCENT_CLASSES[c.accent]}`}
          data-testid={c.testId}
        >
          <div className="text-xs font-medium uppercase tracking-wide text-gray-600">
            {c.label}
          </div>
          <div
            className={`mt-1 text-3xl font-semibold ${VALUE_ACCENT_CLASSES[c.accent]}`}
          >
            {c.value.toLocaleString()}
          </div>
          {c.caption && (
            <div className="mt-2 text-xs text-gray-500">{c.caption}</div>
          )}
        </div>
      ))}
    </div>
  );
}
