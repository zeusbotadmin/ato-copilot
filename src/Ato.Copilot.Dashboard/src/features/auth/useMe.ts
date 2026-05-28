import { useCallback, useEffect, useState } from 'react';
import axios from 'axios';
import type { MeResponse } from './types';

/**
 * Feature 051 T072 [US3] / T135 [US8] — minimal `useMe` hook with
 * an explicit `refetch()` so the impersonation banner can pull a fresh
 * `/me` after Exit / auto-end (Phase 11.3).
 *
 * A heavier React-Query-backed hook will replace this when later phases
 * need session-wide identity state, but the current single-fetch + naive
 * state model is enough for the tenant picker and the impersonation
 * banner.
 */
export interface UseMeResult {
  data: MeResponse | null;
  isLoading: boolean;
  error: Error | null;
  refetch: () => void;
}

export function useMe(): UseMeResult {
  const [data, setData] = useState<MeResponse | null>(null);
  const [isLoading, setLoading] = useState<boolean>(true);
  const [error, setError] = useState<Error | null>(null);
  // Bump to re-run the fetch effect; consumers call refetch() from
  // event handlers (e.g. the impersonation banner's Exit handler).
  const [tick, setTick] = useState(0);

  const refetch = useCallback(() => setTick((t) => t + 1), []);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    void (async () => {
      try {
        const resp = await axios.get('/api/auth/me');
        if (cancelled) return;
        // Envelope: { status, data, metadata }
        const body = resp.data as { status?: string; data?: MeResponse };
        if (body?.status === 'success' && body.data) {
          setData(body.data);
          setError(null);
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
  }, [tick]);

  return { data, isLoading, error, refetch };
}
