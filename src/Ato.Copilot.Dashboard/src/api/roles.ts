import axios from 'axios';
import { attachAuthInterceptor } from '../features/auth/interceptors';
import { getMsalInstance, DEFAULT_API_SCOPES } from '../features/auth/msalInstance';
import apiClient from './client';
import type {
  AssignmentResult,
  AssignOrgRoleBody,
  AssignSystemRoleBody,
  EffectiveRoleResponse,
  RmfRole,
  SystemRolesResponse,
} from '../types/roles';

// ─── Legacy interfaces (preserved for back-compat per FR-024) ──────────────

export interface RoleAssignment {
  id: string;
  role: string;
  userId: string;
  userDisplayName: string | null;
  assignedAt: string;
  assignedBy: string;
}

interface RolesResponse {
  items: RoleAssignment[];
  totalCount: number;
}

export interface AssignRoleBody {
  role: string;
  userDisplayName: string;
  userId?: string;
}

/**
 * Legacy dashboard read of the old RmfRoleAssignments table.
 *
 * @deprecated New consumers should use {@link rolesApi.getSystemRoles} which
 * surfaces the 7-role unified view with precedence-aware source tags
 * (FR-008 / FR-020).
 */
export async function fetchRoles(systemId: string): Promise<RoleAssignment[]> {
  const { data } = await apiClient.get<RolesResponse>(`/systems/${systemId}/roles`);
  return data.items;
}

/**
 * Legacy dashboard write that targets the old RmfRoleAssignments table.
 *
 * @deprecated New consumers should use {@link rolesApi.assignSystemRole} for
 * per-system overrides (FR-010) or {@link rolesApi.assignOrgRole} for Org-scope
 * writes that propagate to every active system (FR-028).
 */
export async function assignRole(systemId: string, body: AssignRoleBody): Promise<RoleAssignment> {
  const { data } = await apiClient.post<RoleAssignment>(`/systems/${systemId}/roles`, body);
  return data;
}

/**
 * Legacy dashboard delete of the old RmfRoleAssignments row.
 *
 * @deprecated New consumers should use {@link rolesApi.removeSystemRole} for
 * per-system override removal (FR-011) or {@link rolesApi.removeOrgRole} for
 * Org-scope removal that cascades to all inherited per-system rows (FR-007).
 */
export async function deleteRole(systemId: string, roleId: string): Promise<void> {
  await apiClient.delete(`/systems/${systemId}/roles/${roleId}`);
}

// ─── Unified roles client (Feature 049) ────────────────────────────────────

/**
 * Dedicated axios client for the Feature-049 unified role endpoints.
 *
 * Mirrors {@link apiClient} for token + simulated-role plumbing but targets
 * the new `/api/roles` route group registered by `SystemRolesEndpoints`.
 */
const rolesClient = axios.create({
  baseURL: '/api/roles',
  headers: { 'Content-Type': 'application/json' },
});

// Feature 051 T053: MSAL bearer injection (silent renewal + 401 retry).
attachAuthInterceptor(rolesClient, getMsalInstance, DEFAULT_API_SCOPES);

// Dev-only simulated-role propagation — orthogonal to the bearer.
rolesClient.interceptors.request.use((config) => {
  if (import.meta.env.DEV) {
    const sim = localStorage.getItem('simulated_role');
    if (sim) {
      config.headers['X-Simulated-Role'] = sim;
    }
  }
  return config;
});

/**
 * Coerces an axios error into the {@link AssignmentResult} envelope so callers
 * can branch on `result.status === 'error'` uniformly. The server returns
 * `{ status: 'error', error: { ... } }` on 400/403/409/503; this helper just
 * unwraps it without re-throwing.
 */
function envelopeFromError(err: unknown): AssignmentResult {
  if (axios.isAxiosError(err) && err.response?.data && typeof err.response.data === 'object') {
    const body = err.response.data as Partial<AssignmentResult>;
    if (body.status === 'error' && body.error) {
      return { status: 'error', error: body.error };
    }
  }
  return {
    status: 'error',
    error: {
      code: 'ROLE_WRITE_THROUGH_FAILED',
      message: err instanceof Error ? err.message : 'Unknown error',
    },
  };
}

export interface RolesApi {
  /** Read the unified 7-role snapshot for a system (FR-008). */
  getSystemRoles(systemId: string): Promise<SystemRolesResponse>;

  /** Resolve the caller's effective role for client-side affordance hiding. */
  getEffectiveRole(): Promise<EffectiveRoleResponse>;

  /** Write a per-system override (FR-010). */
  assignSystemRole(
    systemId: string,
    body: AssignSystemRoleBody,
  ): Promise<AssignmentResult>;

  /** Remove a per-system override (FR-011). Inherited rows return 409. */
  removeSystemRole(
    systemId: string,
    role: RmfRole,
    personId: string,
  ): Promise<AssignmentResult>;

  /** Write an Organization-scope role (FR-028 fans out to all systems). */
  assignOrgRole(body: AssignOrgRoleBody): Promise<AssignmentResult>;

  /** Remove an Organization-scope role (FR-007 cascades inherited rows). */
  removeOrgRole(role: RmfRole, personId: string): Promise<AssignmentResult>;
}

export const rolesApi: RolesApi = {
  async getSystemRoles(systemId) {
    const { data } = await rolesClient.get<{ status: 'success'; data: SystemRolesResponse }>(
      `/system/${systemId}`,
    );
    return data.data;
  },

  async getEffectiveRole() {
    const { data } = await rolesClient.get<{ status: 'success'; data: EffectiveRoleResponse }>(
      '/effective',
    );
    return data.data;
  },

  async assignSystemRole(systemId, body) {
    try {
      const { data } = await rolesClient.post<AssignmentResult>(
        `/system/${systemId}`,
        body,
      );
      return data;
    } catch (err) {
      return envelopeFromError(err);
    }
  },

  async removeSystemRole(systemId, role, personId) {
    try {
      const { data } = await rolesClient.delete<AssignmentResult>(
        `/system/${systemId}/${role}/${personId}`,
      );
      return data;
    } catch (err) {
      return envelopeFromError(err);
    }
  },

  async assignOrgRole(body) {
    try {
      const { data } = await rolesClient.post<AssignmentResult>(
        '/organization',
        body,
      );
      return data;
    } catch (err) {
      return envelopeFromError(err);
    }
  },

  async removeOrgRole(role, personId) {
    try {
      const { data } = await rolesClient.delete<AssignmentResult>(
        `/organization/${role}/${personId}`,
      );
      return data;
    } catch (err) {
      return envelopeFromError(err);
    }
  },
};
