# Phase 1 — Frontend Types Contract: Dashboard Login UX

**Feature**: 051-login
**Plan**: [../plan.md](../plan.md)
**Spec**: [../spec.md](../spec.md)
**HTTP contract**: [./http-api.md](./http-api.md)
**Research**: [../research.md](../research.md)
**Date**: 2026-05-28

This document pins the TypeScript types, MSAL.js configuration shape, and
custom-hook signatures for the dashboard's new login UX. All types live
under `src/Ato.Copilot.Dashboard/src/features/auth/types.ts`.

## 1. Wire types (mirror of [http-api.md](./http-api.md))

```ts
// src/features/auth/types.ts

export type AuthMethodId = 'Cac' | 'Entra' | 'Simulation';
export type AzureCloud = 'AzurePublic' | 'AzureUSGovernment';
export type TenantStatus = 'Active' | 'Suspended' | 'Disabled';

export interface BrandingDescriptor {
  deploymentName: string;
  logoUrl: string | null;
  supportEmail: string | null;
}

export interface AuthMethodDescriptor {
  id: AuthMethodId;
  displayName: string;
}

export interface SimulatedIdentityDescriptor {
  id: string;
  displayName: string;
  persona: string;
  tenantId: string;
  roles: string[];
}

export interface SimulationPanelDescriptor {
  identities: SimulatedIdentityDescriptor[];
}

export interface MsalDescriptor {
  clientId: string;
  authority: string;
  redirectUri: string;
  postLogoutRedirectUri: string;
}

export interface LoginConfig {
  branding: BrandingDescriptor;
  defaultMethod: AuthMethodId;
  enabledMethods: AuthMethodDescriptor[];
  cloud: AzureCloud;
  idleTimeoutMinutes: number;
  rememberTenantCookieDays: number;
  /** Null outside Development. */
  simulation: SimulationPanelDescriptor | null;
  msal: MsalDescriptor;
}

export interface TenantSummary {
  id: string;
  displayName: string;
  status: TenantStatus;
}

export interface PimRoleAssignment {
  name: string;
  expiresAt: string;        // ISO-8601
}

export interface ImpersonationState {
  impersonatedTenant: TenantSummary;
  startedAt: string;
  expiresAt: string;
}

export interface MeResponse {
  oid: string;
  displayName: string;
  persona: string;
  homeTenant: TenantSummary;
  effectiveTenant: TenantSummary;
  isImpersonating: boolean;
  impersonation: ImpersonationState | null;
  pimRoles: PimRoleAssignment[];
  isCspAdmin: boolean;
  isSocAnalyst: boolean;
}

export interface SelectTenantRequest {
  tenantId: string;
  remember?: boolean;
}

export interface SignOutRequest {
  reason?: 'manual' | 'idle_timeout';
}
```

## 2. Error class taxonomy (FR-015)

The SPA maps the server's error envelope `error.errorCode` to a single
client-side `ErrorClass`:

```ts
// src/features/auth/types.ts

export type ErrorClass =
  | 'NoCardInserted'           // CAC: no card / no PIN entered
  | 'CertExpired'              // CAC: cert past notAfter
  | 'CertNotYetValid'          // CAC: cert before notBefore
  | 'CertRevoked'              // CAC: OCSP/CRL revoked
  | 'ClockSkew'                // CAC or Entra: ~5min skew
  | 'NoTenantAssignment'       // Entra: tid known, no Tenants row
  | 'AccountDisabled'          // Entra: account disabled
  | 'MfaFailure'               // Entra: MFA challenge failed
  | 'ConditionalAccessBlock'   // Entra: CA policy
  | 'NetworkFailure';          // Network: cannot reach IdP

export interface ErrorPageProps {
  errorClass: ErrorClass;
  correlationId: string;
  supportEmail: string;
}
```

The error page is server-rendered text on the client; the SPA does not
translate `errorClass` into bespoke messages — it looks up canned copy
per class from `src/features/auth/errorCopy.ts`.

## 3. MSAL.js wiring

### 3.1 Configuration shape

```ts
// src/features/auth/msalConfig.ts
import type { Configuration } from '@azure/msal-browser';

export function buildMsalConfig(login: LoginConfig): Configuration {
  return {
    auth: {
      clientId: login.msal.clientId,
      authority: login.msal.authority,
      redirectUri: login.msal.redirectUri,
      postLogoutRedirectUri: login.msal.postLogoutRedirectUri,
      navigateToLoginRequestUrl: false,  // we handle deep-link redirect ourselves
    },
    cache: {
      cacheLocation: 'localStorage',     // R11: storage event drives login-race coordination
      storeAuthStateInCookie: false,
    },
    system: {
      allowNativeBroker: false,
    },
  };
}
```

### 3.2 Provider mounting

```tsx
// src/main.tsx (sketch)
const login = await fetchLoginConfig();           // GET /api/auth/login-config
const msalInstance = new PublicClientApplication(buildMsalConfig(login));
await msalInstance.initialize();

ReactDOM.createRoot(document.getElementById('root')!).render(
  <MsalProvider instance={msalInstance}>
    <LoginConfigProvider value={login}>
      <App />
    </LoginConfigProvider>
  </MsalProvider>,
);
```

### 3.3 Axios interceptor signature

Replaces every `localStorage.getItem('auth_token')` in
`src/features/*/api.ts` (14 occurrences today):

```ts
// src/features/auth/interceptors.ts
import type { AxiosInstance, InternalAxiosRequestConfig } from 'axios';
import type { IPublicClientApplication } from '@azure/msal-browser';

export function attachAuthInterceptor(
  axiosInstance: AxiosInstance,
  msal: IPublicClientApplication,
  scopes: string[],
): void;

interface UserInputEventDetail {
  source: 'api-success';
}
// Dispatched on EVERY 2xx response (not on silent-renewal retry).
declare global {
  interface WindowEventMap {
    'ato:user-input': CustomEvent<UserInputEventDetail>;
  }
}
```

Per [research.md § R10](../research.md), the interceptor distinguishes
silent renewal from user input by tagging the renewal retry with
`config['_silentRenewal'] = true` and skipping the `ato:user-input`
dispatch on that path.

## 4. Custom hooks

### 4.1 `useIdleTimer`

```ts
// src/features/auth/useIdleTimer.ts
export interface UseIdleTimerResult {
  /** Seconds remaining until idle sign-out. */
  remainingSeconds: number;
  /** Reset the timer (used by the modal "Stay signed in" button). */
  reset: () => void;
}

export function useIdleTimer(timeoutMinutes: number): UseIdleTimerResult;
```

Per [research.md § R10](../research.md):

- Subscribes to `mousemove` / `keydown` / `touchstart` / `click` and
  the custom `'ato:user-input'` event.
- A single chained `setTimeout` reschedules at the next event.
- 60 s before expiry, fires a `'ato:idle-warning'` event so a modal can
  render.
- At expiry, calls `POST /api/auth/signout` with `{ reason: 'idle_timeout' }`.

### 4.2 `useLoginRaceListener`

```ts
// src/features/auth/useLoginRaceListener.ts
export interface UseLoginRaceListenerOptions {
  onLoginCompletedInAnotherTab: () => void;
}

export function useLoginRaceListener(opts: UseLoginRaceListenerOptions): void;
```

Per [research.md § R11](../research.md):

```ts
useEffect(() => {
  const handler = (e: StorageEvent) => {
    if (!e.key) return;
    if (!e.key.startsWith('msal.account.keys')) return;
    const accounts = msalInstance.getAllAccounts();
    if (accounts.length > 0) opts.onLoginCompletedInAnotherTab();
  };
  window.addEventListener('storage', handler);
  return () => window.removeEventListener('storage', handler);
}, [opts.onLoginCompletedInAnotherTab]);
```

### 4.3 `useLoginConfig` and `useMe`

```ts
export function useLoginConfig(): LoginConfig;        // throws if not provided
export function useMe(): { me: MeResponse | null; loading: boolean; refresh: () => Promise<void> };
```

`useMe` is a React Query (`@tanstack/react-query`, already in the
dashboard) wrapper around `GET /api/auth/me` with:

- 5-minute stale time
- Refetch on window focus
- Refetch on `'ato:tenant-changed'` custom event (dispatched by the
  tenant picker after `POST /api/auth/select-tenant`).

### 4.4 `useIdleFormStateBackup` (FR-008 — added per analysis C1)

```ts
// src/features/auth/useIdleFormStateBackup.ts
export interface FormSnapshotSerializer<T> {
  /** Stable id used as part of the localStorage key. */
  formId: string;
  /** Called synchronously on 'ato:idle-warning'; MUST return a JSON-serializable snapshot. */
  serialize: () => T;
}

export interface UseIdleFormStateBackupResult {
  register: <T>(s: FormSnapshotSerializer<T>) => void;
  unregister: (formId: string) => void;
}

export function useIdleFormStateBackup(oid: string): UseIdleFormStateBackupResult;

/** Idempotent purge — call on explicit (non-idle) sign-out. */
export function purgeUnsavedChanges(oid: string): void;
```

Per FR-008: writes snapshots to `localStorage` under key
`ato.unsavedChanges.{oid}.{formId}` with a wall-clock `savedAt` timestamp.
The hook subscribes once to `'ato:idle-warning'` (fired by `useIdleTimer`
60s before idle expiry) and walks every registered serializer
synchronously so the write completes before the user is signed out.
Snapshots are scoped by `oid` so a multi-user device never cross-pollinates.

### 4.5 `RestoreUnsavedChangesPrompt` data flow

```ts
// src/features/auth/RestoreUnsavedChangesPrompt.tsx
export interface UnsavedSnapshot<T = unknown> {
  formId: string;
  savedAt: string;        // ISO-8601
  data: T;
}

export interface RestoreUnsavedChangesPromptProps {
  /** Authenticated user's `oid` — used to filter localStorage keys. */
  oid: string;
}

declare global {
  interface WindowEventMap {
    /** Dispatched when the user clicks "Restore" for a given form. */
    'ato:restore-unsaved': CustomEvent<UnsavedSnapshot>;
  }
}
```

Mounted in `<AppShell />`. On mount, scans `localStorage` for keys
matching `ato.unsavedChanges.{oid}.*`. Renders nothing when no
snapshots exist. Otherwise shows a dismissible prompt listing each
affected form with its saved-at timestamp; "Restore" dispatches
`'ato:restore-unsaved'` and removes the key, "Discard" removes the key.

## 5. Components

| Component | File | Purpose |
|---|---|---|
| `LoginPage` | `src/features/auth/LoginPage.tsx` | The branded `/login` route. Pre-MSAL: pure render of `LoginConfig.branding` + buttons. |
| `LoginCallbackPage` | `src/features/auth/LoginCallbackPage.tsx` | Mounted at `/login/callback`. Awaits `msalInstance.handleRedirectPromise()`, then routes to the deep link OR tenant picker. |
| `TenantPickerPage` | `src/features/auth/TenantPickerPage.tsx` | The `/login/select-tenant` route. Lists user's tenants; the "Remember on this device" checkbox is below the list. |
| `LoginErrorPage` | `src/features/auth/LoginErrorPage.tsx` | The `/login/error` route. Renders canned copy per `ErrorClass` + correlation id + support link. |
| `AccountMenu` | `src/features/auth/AccountMenu.tsx` | Header dropdown — name, persona, home tenant, active PIM role, sign-out button. |
| `ImpersonationBanner` | `src/features/auth/ImpersonationBanner.tsx` | Sticky banner when `me.isImpersonating === true`. |
| `SimulationPanel` | `src/features/auth/SimulationPanel.tsx` | The dev-only identity selector. Route-guarded behind `useLoginConfig().simulation != null`. |
| `IdleWarningModal` | `src/features/auth/IdleWarningModal.tsx` | Renders on `'ato:idle-warning'` event with a countdown + "Stay signed in" button. |
| `RestoreUnsavedChangesPrompt` | `src/features/auth/RestoreUnsavedChangesPrompt.tsx` | FR-008 — surfaces on next sign-in when `localStorage` holds an `ato.unsavedChanges.{oid}.*` key (analysis C1). |

## 6. Routing

```ts
// src/App.tsx (additions)
<Route path="/login" element={<LoginPage />} />
<Route path="/login/callback" element={<LoginCallbackPage />} />
<Route path="/login/select-tenant" element={<RequireAuth><TenantPickerPage /></RequireAuth>} />
<Route path="/login/error" element={<LoginErrorPage />} />

// All authenticated routes wrap in:
<Route element={<RequireAuth><AppShell /></RequireAuth>}>
  {/* existing routes */}
</Route>
```

`RequireAuth` checks `useIsAuthenticated()` from `@azure/msal-react`. On
false, it `loginRedirect({ state: window.location.pathname + window.location.search })`
to preserve the deep link.

## 7. Cross-reference matrix

| FR | Type / hook / component | Section |
|---|---|---|
| FR-001 / FR-002 / FR-003 | `LoginConfig`, `LoginPage` | § 1, § 5 |
| FR-004 | `RequireAuth` deep-link state, `LoginCallbackPage` redirect | § 6 |
| FR-005 / FR-006 | `AccountMenu` sign-out, `LoginErrorPage` post-sign-out copy | § 5 |
| FR-007 / FR-007a | `useIdleTimer`, axios interceptor `_silentRenewal` tag | § 3.3, § 4.1 |
| FR-008 | `useIdleFormStateBackup`, `RestoreUnsavedChangesPrompt` | § 4.4, § 4.5 |
| FR-008 | `useLoginRaceListener` | § 4.2 |
| FR-009 / FR-010 / FR-011 / FR-012 / FR-013 | `TenantPickerPage`, `SelectTenantRequest`, `me.effectiveTenant` | § 1, § 5 |
| FR-014 / FR-015 / FR-016 | `LoginErrorPage`, `ErrorClass`, `errorCopy.ts` | § 2, § 5 |
| FR-023 / FR-024 / FR-025 | `SimulationPanel`, `useLoginConfig().simulation` guard | § 5 |
| FR-026 / FR-027 / FR-028 / FR-029 | `ImpersonationBanner`, `me.impersonation` | § 1, § 5 |
| FR-030 / FR-031 | `AccountMenu`, `useMe` | § 4.3, § 5 |
