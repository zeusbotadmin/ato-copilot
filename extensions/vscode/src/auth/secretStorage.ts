// Feature 051 — VS Code Device-Code Sign-In (T102)
// Contract: specs/051-login/contracts/vscode-extension.md § 5 + § 7
//
// Per-tenant token + account persistence using the VS Code SecretStorage and
// workspaceState APIs. Pure module — uses `import type` only, so unit tests
// can pass plain stub objects without loading the `vscode` runtime.

import type { AccountInfo } from "@azure/msal-node";
import type * as vscode from "vscode";

/** SecretStorage key prefix for access tokens, suffixed with the tenant id. */
export const SECRET_KEY_TOKEN_PREFIX = "ato.auth.token.";
/** SecretStorage key prefix for serialized AccountInfo, suffixed with the tenant id. */
export const SECRET_KEY_ACCOUNT_PREFIX = "ato.auth.account.";
/** workspaceState key for the active tenant id. */
export const STATE_KEY_ACTIVE_TENANT = "ato.auth.activeTenantId";
/**
 * workspaceState key for the list of tenants we have persisted secrets for.
 * Maintained alongside `persistTokenAsync` / `deleteTenantSecretsAsync`
 * because the VS Code SecretStorage API has no `keys()` method (FR-019).
 */
export const STATE_KEY_KNOWN_TENANTS = "ato.auth.knownTenants";

function tokenKey(tenantId: string): string {
  return `${SECRET_KEY_TOKEN_PREFIX}${tenantId}`;
}

function accountKey(tenantId: string): string {
  return `${SECRET_KEY_ACCOUNT_PREFIX}${tenantId}`;
}

/**
 * Slim shape of the bits of `vscode.ExtensionContext` this module needs.
 * Production code passes the real context; tests pass a stub.
 */
export interface SecretStorageContext {
  readonly secrets: Pick<vscode.SecretStorage, "get" | "store" | "delete">;
  readonly workspaceState: Pick<vscode.Memento, "get" | "update">;
}

/** Persist an access token for `tenantId`. Updates the known-tenants index. */
export async function persistTokenAsync(
  context: SecretStorageContext,
  tenantId: string,
  accessToken: string,
): Promise<void> {
  requireTenantId(tenantId);
  await context.secrets.store(tokenKey(tenantId), accessToken);
  await addKnownTenantAsync(context, tenantId);
}

/** Read the access token for `tenantId`, or `undefined` if none is stored. */
export async function readTokenAsync(
  context: SecretStorageContext,
  tenantId: string,
): Promise<string | undefined> {
  requireTenantId(tenantId);
  return context.secrets.get(tokenKey(tenantId));
}

/**
 * Persist the MSAL `AccountInfo` for `tenantId` so the next sign-in can call
 * `pca.acquireTokenSilent({ account, scopes })` before falling back to the
 * device-code flow (R-Summary item 1).
 */
export async function persistAccountAsync(
  context: SecretStorageContext,
  tenantId: string,
  account: AccountInfo,
): Promise<void> {
  requireTenantId(tenantId);
  await context.secrets.store(accountKey(tenantId), JSON.stringify(account));
  await addKnownTenantAsync(context, tenantId);
}

/** Read the persisted `AccountInfo` for `tenantId`, or `undefined`. */
export async function readAccountAsync(
  context: SecretStorageContext,
  tenantId: string,
): Promise<AccountInfo | undefined> {
  requireTenantId(tenantId);
  const raw = await context.secrets.get(accountKey(tenantId));
  if (!raw) return undefined;
  try {
    return JSON.parse(raw) as AccountInfo;
  } catch {
    return undefined;
  }
}

/**
 * Delete all per-tenant secrets (token + account) for `tenantId` and remove
 * it from the known-tenants index. Other tenants' secrets are untouched
 * (FR-019).
 */
export async function deleteTenantSecretsAsync(
  context: SecretStorageContext,
  tenantId: string,
): Promise<void> {
  requireTenantId(tenantId);
  await context.secrets.delete(tokenKey(tenantId));
  await context.secrets.delete(accountKey(tenantId));
  await removeKnownTenantAsync(context, tenantId);
}

/** Read the currently-active tenant id from workspaceState. */
export function getActiveTenantId(
  context: SecretStorageContext,
): string | undefined {
  const value = context.workspaceState.get<string>(STATE_KEY_ACTIVE_TENANT);
  return value && value.length > 0 ? value : undefined;
}

/**
 * Set (or clear, when called with `undefined`) the active tenant id.
 * Status-bar updates and per-request token lookups read this value.
 */
export async function setActiveTenantId(
  context: SecretStorageContext,
  tenantId: string | undefined,
): Promise<void> {
  await context.workspaceState.update(STATE_KEY_ACTIVE_TENANT, tenantId);
}

/**
 * Return the tenants for which a token has been persisted at any point in
 * this workspace. Used by `switchTenantCommand` to populate the QuickPick.
 */
export async function listKnownTenantsAsync(
  context: SecretStorageContext,
): Promise<string[]> {
  return readKnownTenants(context);
}

// ---------- internal helpers ----------

function requireTenantId(tenantId: string): void {
  if (!tenantId || typeof tenantId !== "string") {
    throw new Error("tenantId is required and must be a non-empty string.");
  }
}

function readKnownTenants(context: SecretStorageContext): string[] {
  const value = context.workspaceState.get<string[]>(STATE_KEY_KNOWN_TENANTS);
  return Array.isArray(value) ? value.filter((v) => typeof v === "string") : [];
}

async function addKnownTenantAsync(
  context: SecretStorageContext,
  tenantId: string,
): Promise<void> {
  const known = readKnownTenants(context);
  if (known.includes(tenantId)) return;
  known.push(tenantId);
  await context.workspaceState.update(STATE_KEY_KNOWN_TENANTS, known);
}

async function removeKnownTenantAsync(
  context: SecretStorageContext,
  tenantId: string,
): Promise<void> {
  const known = readKnownTenants(context);
  const next = known.filter((id) => id !== tenantId);
  if (next.length === known.length) return;
  await context.workspaceState.update(STATE_KEY_KNOWN_TENANTS, next);
}
