// Feature 051 — VS Code Device-Code Sign-In (T101)
// Contract: specs/051-login/contracts/vscode-extension.md § 2 + § 2.1
//
// Pure module — no `vscode` runtime imports. Safe to unit test from plain
// Node mocha without launching the Extension Host.

import type { Configuration } from "@azure/msal-node";

/**
 * Login configuration the VS Code extension fetches from the MCP server's
 * `GET /api/auth/login-config` endpoint at command-invocation time.
 *
 * `cloud` (per FR-017 / analysis C14) is REQUIRED so the sign-in command can
 * validate the device-code verification URL against the expected cloud.
 */
export interface VsCodeLoginConfig {
  /** Entra app registration client id (public client). */
  clientId: string;
  /** Full authority URL (e.g. `https://login.microsoftonline.us/<tenantId>`). */
  authority: string;
  /** API scopes for the access token (e.g. `["api://ato-copilot/.default"]`). */
  scopes: string[];
  /** MCP server base URL — used for `/api/auth/me` + `/api/auth/signout` calls. */
  serverBaseUrl: string;
  /** Cloud — drives the verification URL validator below. */
  cloud: "AzurePublic" | "AzureUSGovernment";
}

/**
 * Optional logger hook so the production wrapper can forward MSAL messages
 * to the extension's OutputChannel. Tests pass a no-op.
 */
export interface MsalLogger {
  debug?: (message: string) => void;
}

/**
 * Build the `Configuration` object for `new PublicClientApplication(...)`.
 *
 * Per the contract, PII logging is OFF and the verbose level is used — the
 * extension's OutputChannel filter decides what to surface to the user.
 */
export function buildMsalNodeConfig(
  login: VsCodeLoginConfig,
  logger: MsalLogger = {},
): Configuration {
  return {
    auth: {
      clientId: login.clientId,
      authority: login.authority,
    },
    system: {
      loggerOptions: {
        loggerCallback: (_lvl: number, message: string, _containsPii: boolean) => {
          logger.debug?.(`[msal] ${message}`);
        },
        piiLoggingEnabled: false,
        logLevel: 3, // Verbose
      },
    },
  };
}

/**
 * Cloud → expected device-code verification URL base (FR-017, analysis C14).
 *
 * MUST be called from `signInCommand` before persisting any token: a tenant
 * misconfigured with the public authority but the gov cloud (or vice versa)
 * would otherwise silently route the user to the wrong endpoint.
 */
export function getExpectedVerificationUriBase(
  cloud: "AzurePublic" | "AzureUSGovernment",
): string {
  switch (cloud) {
    case "AzurePublic":
      return "https://microsoft.com/devicelogin";
    case "AzureUSGovernment":
      return "https://microsoft.us/devicelogin";
    default: {
      // Exhaustiveness guard — TS will flag any new enum value.
      const exhaustive: never = cloud;
      throw new Error(`Unknown cloud value: ${String(exhaustive)}`);
    }
  }
}

/**
 * Validate a `DeviceCodeResponse.verificationUri` (or `.verificationUriComplete`)
 * against the cloud the extension is configured for.
 *
 * Throws a `CloudVerificationMismatchError` if the URI does not start with the
 * expected base. The caller is responsible for aborting sign-in and NOT
 * persisting any tokens when this fires.
 *
 * The `https://` prefix comparison is case-insensitive on host (RFC 3986) but
 * exact on path — `microsoft.com/devicelogin` vs `microsoft.us/devicelogin` is
 * a path-level distinction the comparison MUST honor.
 */
export function validateVerificationUri(
  verificationUri: string,
  cloud: "AzurePublic" | "AzureUSGovernment",
): void {
  const expected = getExpectedVerificationUriBase(cloud);
  // Normalize the inbound URL host case (path is case-sensitive).
  let normalized: string;
  try {
    const u = new URL(verificationUri);
    normalized = `${u.protocol}//${u.host.toLowerCase()}${u.pathname}`;
  } catch {
    throw new CloudVerificationMismatchError(verificationUri, expected);
  }
  if (!normalized.startsWith(expected)) {
    throw new CloudVerificationMismatchError(verificationUri, expected);
  }
}

/**
 * Thrown by `validateVerificationUri` when the device-code response's
 * verification URL does not match the configured cloud.
 */
export class CloudVerificationMismatchError extends Error {
  public readonly received: string;
  public readonly expected: string;
  constructor(received: string, expected: string) {
    super(
      `Device-code verification URL "${received}" does not match the expected ` +
        `base "${expected}" for the configured cloud. Aborting sign-in to ` +
        `prevent routing credentials to the wrong cloud.`,
    );
    this.name = "CloudVerificationMismatchError";
    this.received = received;
    this.expected = expected;
  }
}
