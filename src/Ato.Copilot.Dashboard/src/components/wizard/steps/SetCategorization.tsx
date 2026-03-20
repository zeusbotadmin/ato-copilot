import { useState, useEffect, useRef } from 'react';
import apiClient from '../../../api/client';
import type { Sp80060InfoType } from '../../../types/dashboard';
import infoTypesData from '../../../data/sp800-60-information-types.json';

interface SelectedInfoType {
  sp80060Id: string;
  name: string;
  category: string;
  confidentialityImpact: string;
  integrityImpact: string;
  availabilityImpact: string;
  usesProvisional: boolean;
}

interface SetCategorizationProps {
  systemId: string;
  onNext: () => void;
  onErrors: (errors: Record<string, string[]>) => void;
}

const IMPACT_OPTIONS = ['Low', 'Moderate', 'High'];

function highWaterMark(levels: string[]): string {
  if (levels.includes('High')) return 'High';
  if (levels.includes('Moderate')) return 'Moderate';
  return 'Low';
}

export default function SetCategorization({ systemId, onNext, onErrors }: SetCategorizationProps) {
  const allTypes: Sp80060InfoType[] = (infoTypesData as { informationTypes: Sp80060InfoType[] }).informationTypes;
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<SelectedInfoType[]>([]);
  const [isNSS, setIsNSS] = useState(false);
  const [justification, setJustification] = useState('');
  const [saving, setSaving] = useState(false);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [filteredTypes, setFilteredTypes] = useState(allTypes);

  // Debounced search
  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      if (!search.trim()) {
        setFilteredTypes(allTypes);
      } else {
        const q = search.toLowerCase();
        setFilteredTypes(allTypes.filter((t) => t.name.toLowerCase().includes(q) || t.category.toLowerCase().includes(q) || t.id.toLowerCase().includes(q)));
      }
    }, 300);
    return () => { if (debounceRef.current) clearTimeout(debounceRef.current); };
  }, [search, allTypes]);

  const selectedIds = new Set(selected.map((s) => s.sp80060Id));

  const handleSelect = (t: Sp80060InfoType) => {
    if (selectedIds.has(t.id)) return;
    setSelected((prev) => [
      ...prev,
      {
        sp80060Id: t.id,
        name: t.name,
        category: t.category,
        confidentialityImpact: t.confidentiality,
        integrityImpact: t.integrity,
        availabilityImpact: t.availability,
        usesProvisional: true,
      },
    ]);
  };

  const handleRemove = (id: string) => {
    setSelected((prev) => prev.filter((s) => s.sp80060Id !== id));
  };

  const handleOverride = (id: string, field: keyof SelectedInfoType, value: string) => {
    setSelected((prev) =>
      prev.map((s) => (s.sp80060Id === id ? { ...s, [field]: value, usesProvisional: false } : s)),
    );
  };

  // Compute FIPS 199
  const overallC = selected.length > 0 ? highWaterMark(selected.map((s) => s.confidentialityImpact)) : 'N/A';
  const overallI = selected.length > 0 ? highWaterMark(selected.map((s) => s.integrityImpact)) : 'N/A';
  const overallA = selected.length > 0 ? highWaterMark(selected.map((s) => s.availabilityImpact)) : 'N/A';
  const overallFips = selected.length > 0 ? highWaterMark([overallC, overallI, overallA]) : 'N/A';

  const handleFinish = async () => {
    setSaving(true);
    try {
      await apiClient.post(`/systems/${systemId}/categorization`, {
        isNationalSecuritySystem: isNSS,
        justification: justification.trim() || undefined,
        informationTypes: selected.map((s) => ({
          sp80060Id: s.sp80060Id,
          name: s.name,
          category: s.category,
          confidentialityImpact: s.confidentialityImpact,
          integrityImpact: s.integrityImpact,
          availabilityImpact: s.availabilityImpact,
          usesProvisional: s.usesProvisional,
        })),
      });
      onNext();
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Failed to save categorization';
      onErrors({ _form: [msg] });
    } finally {
      setSaving(false);
    }
  };

  // Group types by category
  const categories = [...new Set(filteredTypes.map((t) => t.category))];

  return (
    <div>
      <h2 className="text-xl font-semibold text-gray-900 mb-1">Step 7: Set Categorization</h2>
      <p className="text-sm text-gray-500 mb-6">Select SP 800-60 information types and review the FIPS 199 categorization.</p>

      {/* FIPS 199 Summary */}
      {selected.length > 0 && (
        <div className="mb-6 rounded-md border border-blue-200 bg-blue-50 p-4">
          <h3 className="text-sm font-medium text-blue-900 mb-2">FIPS 199 Overall Categorization: <span className="font-bold">{overallFips}</span></h3>
          <div className="flex gap-6 text-sm">
            <span>Confidentiality: <strong>{overallC}</strong></span>
            <span>Integrity: <strong>{overallI}</strong></span>
            <span>Availability: <strong>{overallA}</strong></span>
          </div>
        </div>
      )}

      {/* Selected types */}
      {selected.length > 0 && (
        <div className="mb-6">
          <h3 className="text-sm font-medium text-gray-700 mb-2">Selected Information Types ({selected.length})</h3>
          <div className="overflow-hidden rounded-md border border-gray-200">
            <table className="min-w-full divide-y divide-gray-200 text-sm">
              <thead className="bg-gray-50">
                <tr>
                  <th className="px-3 py-2 text-left text-xs font-medium text-gray-500">Info Type</th>
                  <th className="px-3 py-2 text-center text-xs font-medium text-gray-500">C</th>
                  <th className="px-3 py-2 text-center text-xs font-medium text-gray-500">I</th>
                  <th className="px-3 py-2 text-center text-xs font-medium text-gray-500">A</th>
                  <th className="px-3 py-2 w-8" />
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {selected.map((s) => (
                  <tr key={s.sp80060Id}>
                    <td className="px-3 py-2">
                      <span className="font-medium">{s.name}</span>
                      <span className="ml-1 text-xs text-gray-400">{s.sp80060Id}</span>
                    </td>
                    <td className="px-3 py-2 text-center">
                      <select value={s.confidentialityImpact} onChange={(e) => handleOverride(s.sp80060Id, 'confidentialityImpact', e.target.value)} className="rounded border border-gray-300 px-1 py-0.5 text-xs">
                        {IMPACT_OPTIONS.map((o) => <option key={o} value={o}>{o}</option>)}
                      </select>
                    </td>
                    <td className="px-3 py-2 text-center">
                      <select value={s.integrityImpact} onChange={(e) => handleOverride(s.sp80060Id, 'integrityImpact', e.target.value)} className="rounded border border-gray-300 px-1 py-0.5 text-xs">
                        {IMPACT_OPTIONS.map((o) => <option key={o} value={o}>{o}</option>)}
                      </select>
                    </td>
                    <td className="px-3 py-2 text-center">
                      <select value={s.availabilityImpact} onChange={(e) => handleOverride(s.sp80060Id, 'availabilityImpact', e.target.value)} className="rounded border border-gray-300 px-1 py-0.5 text-xs">
                        {IMPACT_OPTIONS.map((o) => <option key={o} value={o}>{o}</option>)}
                      </select>
                    </td>
                    <td className="px-3 py-2 text-center">
                      <button onClick={() => handleRemove(s.sp80060Id)} className="text-red-500 hover:text-red-700 text-xs">✕</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* Search */}
      <div className="mb-3">
        <input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
          placeholder="Search information types..."
        />
      </div>

      {/* Info type list grouped by category */}
      <div className="border border-gray-200 rounded-md max-h-56 overflow-y-auto mb-6">
        {categories.map((cat) => (
          <div key={cat}>
            <div className="sticky top-0 bg-gray-100 px-3 py-1 text-xs font-semibold text-gray-600">{cat}</div>
            {filteredTypes
              .filter((t) => t.category === cat)
              .map((t) => (
                <button
                  key={t.id}
                  onClick={() => handleSelect(t)}
                  disabled={selectedIds.has(t.id)}
                  className={`w-full text-left px-3 py-1.5 text-sm border-b border-gray-100 hover:bg-blue-50 ${selectedIds.has(t.id) ? 'opacity-50' : ''}`}
                >
                  <span className="text-xs text-gray-400 mr-2">{t.id}</span>
                  {t.name}
                  <span className="float-right text-xs text-gray-400">{t.confidentiality}/{t.integrity}/{t.availability}</span>
                </button>
              ))}
          </div>
        ))}
      </div>

      {/* Options */}
      <div className="space-y-3 mb-6">
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={isNSS} onChange={(e) => setIsNSS(e.target.checked)} className="rounded" />
          National Security System
        </label>
        <div>
          <label className="mb-1 block text-sm font-medium text-gray-700">Justification</label>
          <textarea
            value={justification}
            onChange={(e) => setJustification(e.target.value)}
            rows={2}
            className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
            placeholder="Optional justification for categorization decisions"
          />
        </div>
      </div>

      <div className="flex justify-end gap-3 mt-6">
        <button
          onClick={onNext}
          className="rounded-md border border-gray-300 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50"
        >
          Skip
        </button>
        <button
          onClick={handleFinish}
          disabled={saving}
          className="rounded-md bg-blue-600 px-6 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
        >
          {saving ? 'Saving...' : 'Save & Next'}
        </button>
      </div>
    </div>
  );
}
