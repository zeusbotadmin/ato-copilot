import { useEffect, useMemo, useState, type ReactElement, type ReactNode } from 'react';
import {
  archiveCspInheritedCapability,
  listCspInheritedCapabilities,
  patchCspInheritedCapability,
  remapCspInheritedComponent,
  reviewCspInheritedCapability,
  type CspInheritedCapability,
  type CspInheritedComponent,
} from './api';

interface Props {
  componentId: string;
  capabilityId: string;
  /** Display-only name + type of the parent component (passed in by the
   *  caller so the drawer doesn't have to re-fetch the component just to
   *  render the header). */
  componentName: string;
  componentType: CspInheritedComponent['componentType'];
  canManage: boolean;
  onClose: () => void;
  /** Called whenever the drawer mutates the capability so the parent list
   *  can refresh (or splice the row). */
  onMutated: () => void;
}

/**
 * `CapabilityDetailDrawer` — Feature 048 / US9 follow-on.
 *
 * Right-side drawer showing the full record for a single CSP-inherited
 * capability with full CRUD (Edit / Archive / Remap-parent-component) for
 * CSP-Admin users. Read-only for non-CSP-Admin callers. Mirrors
 * `ComponentDetailDrawer.tsx` chrome so the two surfaces behave the same.
 *
 * NOTE: A per-capability "Remap" backend operation does not exist — the AI
 * mapping pipeline operates on a whole component. The Remap button on this
 * drawer therefore re-maps the **parent component** (with
 * `preserveHumanMappings = true` by default), which is the closest
 * equivalent. The component-level remap is what the user means by
 * "Remap (for components)".
 */
export default function CapabilityDetailDrawer({
  componentId,
  capabilityId,
  componentName,
  componentType,
  canManage,
  onClose,
  onMutated,
}: Props): ReactElement {
  const [capability, setCapability] = useState<CspInheritedCapability | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Edit-form state
  const [editing, setEditing] = useState(false);
  const [editName, setEditName] = useState('');
  const [editDescription, setEditDescription] = useState('');
  const [editControls, setEditControls] = useState('');

  // Review-form state (only relevant when status === 'NeedsReview')
  const [reviewing, setReviewing] = useState(false);
  const [reviewControls, setReviewControls] = useState('');
  const [reviewerNote, setReviewerNote] = useState('');

  // Initial fetch — there is no dedicated GET-single-capability endpoint;
  // pull the parent component's capability list and pick this row.
  useEffect(() => {
    let cancelled = false;
    setCapability(null);
    setError(null);
    void (async () => {
      try {
        const caps = await listCspInheritedCapabilities(componentId);
        if (cancelled) return;
        const found = caps.find((c) => c.id === capabilityId);
        if (!found) {
          setError('Capability not found — it may have been archived or remapped away.');
          return;
        }
        setCapability(found);
        setEditName(found.name);
        setEditDescription(found.description ?? '');
        setEditControls(found.mappedNistControlIds.join(', '));
        setReviewControls(found.mappedNistControlIds.join(', '));
      } catch (err) {
        const e = err as { errorCode?: string; message?: string };
        if (!cancelled) setError(e?.message ?? 'Failed to load capability.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [componentId, capabilityId]);

  const parsedEditControls = useMemo(() => parseControlIds(editControls), [editControls]);
  const parsedReviewControls = useMemo(() => parseControlIds(reviewControls), [reviewControls]);

  const handleSaveEdit = async () => {
    if (!capability) return;
    if (editName.trim().length === 0 || editDescription.trim().length === 0) {
      setError('Name and description are required.');
      return;
    }
    if (parsedEditControls.length === 0) {
      setError('Provide at least one NIST control ID (e.g. AC-2, AC-2(1)).');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const updated = await patchCspInheritedCapability(
        componentId,
        capability.id,
        {
          name: editName.trim(),
          description: editDescription.trim(),
          mappedNistControlIds: parsedEditControls,
        },
        capability.rowVersion ?? undefined,
      );
      setCapability(updated);
      setEditing(false);
      onMutated();
    } catch (err) {
      const e = err as { errorCode?: string; message?: string };
      setError(
        e?.errorCode === 'ROW_VERSION_MISMATCH'
          ? 'This capability was changed by another user. Reload to see the latest.'
          : (e?.message ?? 'Failed to save changes.'),
      );
    } finally {
      setBusy(false);
    }
  };

  const handleReview = async () => {
    if (!capability) return;
    if (parsedReviewControls.length === 0) {
      setError('Provide at least one NIST control ID to resolve the review.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const updated = await reviewCspInheritedCapability(componentId, capability.id, {
        mappedNistControlIds: parsedReviewControls,
        reviewerNote: reviewerNote.trim() || undefined,
      });
      setCapability(updated);
      setReviewing(false);
      onMutated();
    } catch (err) {
      const e = err as { errorCode?: string; message?: string };
      setError(e?.message ?? 'Failed to complete review.');
    } finally {
      setBusy(false);
    }
  };

  const handleArchive = async () => {
    if (!capability) return;
    if (!window.confirm(`Archive capability “${capability.name}”?`)) return;
    setBusy(true);
    setError(null);
    try {
      await archiveCspInheritedCapability(componentId, capability.id);
      onMutated();
      onClose();
    } catch (err) {
      const e = err as { errorCode?: string; message?: string };
      setError(e?.message ?? 'Failed to archive capability.');
    } finally {
      setBusy(false);
    }
  };

  const handleRemap = async () => {
    if (!capability) return;
    if (
      !window.confirm(
        `Remap the parent component “${componentName}”? AI mappings will be regenerated; existing human-mapped capabilities are preserved.`,
      )
    ) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      // Per-capability remap doesn't exist server-side — fall back to the
      // component-level remap which is what the user means by
      // "Remap (for components)".
      await remapCspInheritedComponent(componentId);
      onMutated();
      // The remap may have replaced this capability id; close so the parent
      // list reload surfaces the fresh rows.
      onClose();
    } catch (err) {
      const e = err as { errorCode?: string; message?: string };
      setError(
        e?.errorCode === 'AI_MAPPING_UNAVAILABLE'
          ? 'AI capability mapping service is unavailable. Try again later.'
          : (e?.message ?? 'Failed to remap.'),
      );
    } finally {
      setBusy(false);
    }
  };

  return (
    <aside
      className="fixed inset-y-0 right-0 z-40 flex w-full max-w-xl flex-col border-l border-gray-200 bg-white shadow-xl"
      aria-label="Capability detail"
      data-testid="csp-capability-drawer"
    >
      {/* Header */}
      <header className="flex items-start justify-between gap-2 border-b border-gray-200 px-4 py-3">
        <div className="min-w-0 flex-1">
          <h2 className="truncate text-base font-semibold text-gray-900">
            {capability?.name ?? 'Loading…'}
          </h2>
          <p className="text-xs text-gray-500">
            {componentName} · {componentType}
            {capability && (
              <>
                {' '}
                · <StatusBadge status={capability.status} />
              </>
            )}
          </p>
        </div>
        <button
          type="button"
          onClick={onClose}
          className="rounded-md p-1 text-gray-500 hover:bg-gray-100"
          aria-label="Close detail panel"
          data-testid="csp-capability-drawer-close"
        >
          ✕
        </button>
      </header>

      {/* Body */}
      <div className="flex-1 overflow-y-auto px-4 py-3">
        {error && (
          <div
            role="alert"
            className="mb-3 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700"
          >
            {error}
          </div>
        )}

        {!capability ? (
          <p className="text-sm text-gray-500">Loading capability…</p>
        ) : (
          <div className="space-y-5">
            {/* Edit form */}
            {editing ? (
              <div className="space-y-3 rounded-md border border-indigo-200 bg-indigo-50 p-3">
                <label className="block text-xs font-medium text-gray-700">
                  Name
                  <input
                    type="text"
                    value={editName}
                    onChange={(e) => setEditName(e.target.value)}
                    maxLength={256}
                    className="mt-1 block w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                    data-testid="csp-capability-edit-name"
                  />
                </label>
                <label className="block text-xs font-medium text-gray-700">
                  Description
                  <textarea
                    value={editDescription}
                    onChange={(e) => setEditDescription(e.target.value)}
                    rows={3}
                    maxLength={2000}
                    className="mt-1 block w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                    data-testid="csp-capability-edit-description"
                  />
                </label>
                <label className="block text-xs font-medium text-gray-700">
                  Mapped NIST control IDs
                  <input
                    type="text"
                    value={editControls}
                    onChange={(e) => setEditControls(e.target.value)}
                    placeholder="AC-2, AC-2(1), SC-7"
                    className="mt-1 block w-full rounded-md border border-gray-300 px-2 py-1.5 font-mono text-sm focus:border-indigo-500 focus:outline-none focus:ring-1 focus:ring-indigo-500"
                    data-testid="csp-capability-edit-controls"
                  />
                  <span className="mt-0.5 block text-[11px] text-gray-500">
                    Comma- or space-separated. Saving stamps this row as a
                    human mapping (mappedBy=User) and resolves any pending
                    NeedsReview.
                  </span>
                </label>
                <div className="flex justify-end gap-2">
                  <button
                    type="button"
                    onClick={() => setEditing(false)}
                    disabled={busy}
                    className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
                  >
                    Cancel
                  </button>
                  <button
                    type="button"
                    onClick={handleSaveEdit}
                    disabled={
                      busy ||
                      editName.trim().length === 0 ||
                      editDescription.trim().length === 0 ||
                      parsedEditControls.length === 0
                    }
                    className="rounded-md bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-indigo-700 disabled:cursor-not-allowed disabled:bg-indigo-300"
                    data-testid="csp-capability-edit-save"
                  >
                    {busy ? 'Saving…' : 'Save changes'}
                  </button>
                </div>
              </div>
            ) : (
              <dl className="divide-y divide-gray-100 rounded-md border border-gray-200 text-sm">
                <Row label="Description">
                  {capability.description ? (
                    capability.description
                  ) : (
                    <em className="text-gray-400">(none)</em>
                  )}
                </Row>
                <Row label="NIST controls">
                  {capability.mappedNistControlIds.length === 0 ? (
                    <em className="text-gray-400">(none)</em>
                  ) : (
                    <div className="flex flex-wrap gap-1">
                      {capability.mappedNistControlIds.map((id) => (
                        <span
                          key={id}
                          className="inline-flex items-center rounded bg-indigo-50 px-1.5 py-0.5 text-[11px] font-medium text-indigo-700"
                        >
                          {id}
                        </span>
                      ))}
                    </div>
                  )}
                </Row>
                <Row label="Mapped by">
                  {capability.mappedBy}
                  {typeof capability.mappingConfidence === 'number' && (
                    <span className="ml-2 text-xs text-gray-500">
                      (confidence {(capability.mappingConfidence * 100).toFixed(0)}%)
                    </span>
                  )}
                </Row>
                {capability.status === 'NeedsReview' && capability.mappingFailureReason && (
                  <Row label="Review reason">
                    <span className="text-amber-700">{capability.mappingFailureReason}</span>
                  </Row>
                )}
                {capability.createdAt && (
                  <Row label="Created">
                    {new Date(capability.createdAt).toLocaleString()}
                    {capability.createdBy && <> by {capability.createdBy}</>}
                  </Row>
                )}
                {capability.reviewedAt && (
                  <Row label="Last reviewed">
                    {new Date(capability.reviewedAt).toLocaleString()}
                    {capability.reviewedBy && <> by {capability.reviewedBy}</>}
                  </Row>
                )}
                {capability.reviewerNote && (
                  <Row label="Reviewer note">{capability.reviewerNote}</Row>
                )}
              </dl>
            )}

            {/* Review form — only when this capability needs review */}
            {canManage && capability.status === 'NeedsReview' && reviewing && (
              <div className="space-y-3 rounded-md border border-amber-200 bg-amber-50 p-3">
                <h3 className="text-sm font-semibold text-gray-900">Resolve review</h3>
                <label className="block text-xs font-medium text-gray-700">
                  Mapped NIST control IDs
                  <input
                    type="text"
                    value={reviewControls}
                    onChange={(e) => setReviewControls(e.target.value)}
                    placeholder="AC-2, AC-2(1), SC-7"
                    className="mt-1 block w-full rounded-md border border-gray-300 px-2 py-1.5 font-mono text-sm focus:border-amber-500 focus:outline-none focus:ring-1 focus:ring-amber-500"
                    data-testid="csp-capability-review-controls"
                  />
                </label>
                <label className="block text-xs font-medium text-gray-700">
                  Reviewer note (optional)
                  <textarea
                    value={reviewerNote}
                    onChange={(e) => setReviewerNote(e.target.value)}
                    rows={2}
                    maxLength={2000}
                    className="mt-1 block w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm focus:border-amber-500 focus:outline-none focus:ring-1 focus:ring-amber-500"
                    data-testid="csp-capability-review-note"
                  />
                </label>
                <div className="flex justify-end gap-2">
                  <button
                    type="button"
                    onClick={() => setReviewing(false)}
                    disabled={busy}
                    className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
                  >
                    Cancel
                  </button>
                  <button
                    type="button"
                    onClick={handleReview}
                    disabled={busy || parsedReviewControls.length === 0}
                    className="rounded-md bg-amber-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-amber-700 disabled:cursor-not-allowed disabled:bg-amber-300"
                    data-testid="csp-capability-review-submit"
                  >
                    {busy ? 'Resolving…' : 'Resolve review'}
                  </button>
                </div>
              </div>
            )}

            {/* Action toolbar — CSP-Admin only */}
            {canManage && !editing && !reviewing && (
              <div className="flex flex-wrap items-center gap-2">
                <button
                  type="button"
                  onClick={() => {
                    setEditName(capability.name);
                    setEditDescription(capability.description ?? '');
                    setEditControls(capability.mappedNistControlIds.join(', '));
                    setEditing(true);
                  }}
                  disabled={busy || capability.status === 'Archived'}
                  className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:cursor-not-allowed disabled:opacity-50"
                  data-testid="csp-capability-edit-toggle"
                  title={capability.status === 'Archived' ? 'Cannot edit an archived capability' : undefined}
                >
                  Edit
                </button>
                {capability.status === 'NeedsReview' && (
                  <button
                    type="button"
                    onClick={() => {
                      setReviewControls(capability.mappedNistControlIds.join(', '));
                      setReviewerNote('');
                      setReviewing(true);
                    }}
                    disabled={busy}
                    className="rounded-md bg-amber-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-amber-700 disabled:cursor-not-allowed disabled:bg-amber-300"
                    data-testid="csp-capability-review-toggle"
                  >
                    Resolve review
                  </button>
                )}
                {capability.status !== 'Archived' && (
                  <button
                    type="button"
                    onClick={handleArchive}
                    disabled={busy}
                    className="rounded-md border border-red-300 bg-white px-3 py-1.5 text-xs font-medium text-red-700 hover:bg-red-50 disabled:opacity-50"
                    data-testid="csp-capability-archive"
                  >
                    Archive
                  </button>
                )}
                <button
                  type="button"
                  onClick={handleRemap}
                  disabled={busy}
                  className="rounded-md border border-indigo-300 bg-white px-3 py-1.5 text-xs font-medium text-indigo-700 hover:bg-indigo-50 disabled:opacity-50"
                  title="Re-run AI capability mapping on the parent component"
                  data-testid="csp-capability-remap"
                >
                  Remap parent component
                </button>
              </div>
            )}
          </div>
        )}
      </div>
    </aside>
  );
}

function parseControlIds(raw: string): string[] {
  return raw
    .split(/[,\s]+/)
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

function Row({ label, children }: { label: string; children: ReactNode }): ReactElement {
  return (
    <div className="grid grid-cols-3 gap-3 px-3 py-2">
      <dt className="font-medium text-gray-700">{label}</dt>
      <dd className="col-span-2 text-gray-900">{children}</dd>
    </div>
  );
}

function StatusBadge({ status }: { status: CspInheritedCapability['status'] }): ReactElement {
  const palette =
    status === 'Mapped'
      ? 'bg-emerald-100 text-emerald-800'
      : status === 'NeedsReview'
        ? 'bg-amber-100 text-amber-800'
        : 'bg-gray-200 text-gray-700'; // Archived
  const label = status === 'NeedsReview' ? 'Needs review' : status;
  return (
    <span className={`inline-flex items-center rounded px-2 py-0.5 text-xs font-medium ${palette}`}>
      {label}
    </span>
  );
}
