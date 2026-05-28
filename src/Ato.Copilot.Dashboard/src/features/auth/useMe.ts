import { useEffect, useState } from 'react';
import axios from 'axios';
import type { MeResponse } from './types';

/**
 * Feature 051 T072 [US3] — minimal `useMe` hook for the tenant picker.
 *
 * A heavier React-Query-backed hook will replace this when later phases
 * need session-wide identity state (US8 wires the impersonation cookie
 * here too). For now the picker is the only consumer, so a single
 * fetch on mount + naive in-memory state is sufficient.
 */
export interface UseMeResult {
  data: MeResponse | null;
  isLoading: boolean;
  error: Error | null;
}

export function useMe(): UseMeResult {
  const [data, setData] = useState<MeResponse | null>(null);
  const [isLoading, setLoading] = useState<boolean>(true);
  const [error, setError] = useState<Error | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const resp = await axios.get('/api/auth/me');
        if (cancelled) return;
        // Envelope: { status, data, metadata }
        const body = resp.data as { status?: string; data?: MeResponse };
        if (body?.status === 'success' && body.data) {
          setData(body.data);
        } else {
          setError(new Error('Unexpected /api/auth/me envelope'));
        }
      } catch (err) {
        if (cancelled) return;
        setError(err instanceof Error ? err : new Error(String(err)));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  return { data, isLoading, error };
}
