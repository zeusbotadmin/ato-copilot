import axios from 'axios';
import type { ErrorResponse } from '../types/dashboard';
import { attachAuthInterceptor } from '../features/auth/interceptors';
import { getMsalInstance, DEFAULT_API_SCOPES } from '../features/auth/msalInstance';

/**
 * Feature 051 T053 [US1] migration:
 *   The Authorization header is now sourced from MSAL via the shared
 *   `attachAuthInterceptor` (silent token renewal + 401 retry +
 *   `loginRedirect` on second 401). The legacy
 *   `localStorage.getItem('auth_token')` read and the bespoke
 *   "retry-without-auth" recovery have been removed — MSAL is the
 *   single authoritative token source.
 *
 *   The dev-only `X-Simulated-Role` header is preserved because it is
 *   orthogonal to the bearer token (FR-048).
 */

const apiClient = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || '/api/dashboard',
  headers: { 'Content-Type': 'application/json' },
});

// MSAL-backed bearer injection (silent renewal + 401 → loginRedirect).
attachAuthInterceptor(apiClient, getMsalInstance, DEFAULT_API_SCOPES);

// Dev-only simulated-role header (orthogonal to the bearer; FR-048).
apiClient.interceptors.request.use((config) => {
  try {
    const raw = localStorage.getItem('ato-dashboard-settings');
    if (raw) {
      const settings = JSON.parse(raw) as { role?: string };
      if (settings.role) {
        config.headers['X-Simulated-Role'] = settings.role;
      }
    }
  } catch {
    // Ignore parse errors
  }
  return config;
});

// Surface server-side ErrorResponse envelopes to feature callers.
apiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    if (axios.isAxiosError(error) && error.response?.data) {
      const errorResponse = error.response.data as ErrorResponse;
      return Promise.reject(errorResponse);
    }
    return Promise.reject(error);
  },
);

export default apiClient;
