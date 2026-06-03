// Feature 051 — VS Code Device-Code Sign-In (T103, RED → GREEN)
// Contract: specs/051-login/contracts/vscode-extension.md § 5
//
// Verifies the per-tenant SecretStorage helpers using a plain in-memory
// stub of `vscode.ExtensionContext.secrets` + `workspaceState`. The
// module under test uses `import type * as vscode from "vscode"` so we
// can avoid pulling in the Extension Host at all.

import { expect } from "chai";
import type { AccountInfo } from "@azure/msal-node";

import {
  deleteTenantSecretsAsync,
  getActiveTenantId,
  listKnownTenantsAsync,
  persistAccountAsync,
  persistTokenAsync,
  readAccountAsync,
  readTokenAsync,
  SECRET_KEY_ACCOUNT_PREFIX,
  SECRET_KEY_TOKEN_PREFIX,
  setActiveTenantId,
  STATE_KEY_ACTIVE_TENANT,
  STATE_KEY_KNOWN_TENANTS,
  type SecretStorageContext,
} from "../../src/auth/secretStorage";

interface StubContext extends SecretStorageContext {
  secretsMap: Map<string, string>;
  stateMap: Map<string, unknown>;
}

function makeContext(): StubContext {
  const secretsMap = new Map<string, string>();
  const stateMap = new Map<string, unknown>();
  return {
    secretsMap,
    stateMap,
    secrets: {
      get: async (key: string) => secretsMap.get(key),
      store: async (key: string, value: string) => {
        secretsMap.set(key, value);
      },
      delete: async (key: string) => {
        secretsMap.delete(key);
      },
    },
    workspaceState: {
      get: <T,>(key: string): T | undefined => stateMap.get(key) as T | undefined,
      update: async (key: string, value: unknown) => {
        if (value === undefined) {
          stateMap.delete(key);
        } else {
          stateMap.set(key, value);
        }
      },
    },
  };
}

const TENANT_A = "00000000-0000-0000-0000-00000000000a";
const TENANT_B = "00000000-0000-0000-0000-00000000000b";

const accountA: AccountInfo = {
  homeAccountId: `${TENANT_A}.user`,
  environment: "login.microsoftonline.us",
  tenantId: TENANT_A,
  username: "user@tenant-a.gov",
  localAccountId: "user-a",
};

describe("Feature 051 — auth/secretStorage", () => {
  describe("persistTokenAsync + readTokenAsync", () => {
    it("round-trips a token under the per-tenant key", async () => {
      // Arrange
      const ctx = makeContext();

      // Act
      await persistTokenAsync(ctx, TENANT_A, "token-A");
      const read = await readTokenAsync(ctx, TENANT_A);

      // Assert
      expect(read).to.equal("token-A");
      expect(ctx.secretsMap.get(`${SECRET_KEY_TOKEN_PREFIX}${TENANT_A}`)).to.equal(
        "token-A",
      );
    });

    it("returns undefined when no token is stored for the tenant", async () => {
      // Arrange
      const ctx = makeContext();

      // Act
      const read = await readTokenAsync(ctx, TENANT_A);

      // Assert
      expect(read).to.equal(undefined);
    });

    it("rejects empty tenant ids", async () => {
      // Arrange
      const ctx = makeContext();

      // Act + Assert
      let threw = false;
      try {
        await persistTokenAsync(ctx, "", "token");
      } catch {
        threw = true;
      }
      expect(threw).to.equal(true);
    });
  });

  describe("persistAccountAsync + readAccountAsync", () => {
    it("round-trips an AccountInfo per tenant", async () => {
      // Arrange
      const ctx = makeContext();

      // Act
      await persistAccountAsync(ctx, TENANT_A, accountA);
      const read = await readAccountAsync(ctx, TENANT_A);

      // Assert
      expect(read).to.deep.equal(accountA);
      expect(
        ctx.secretsMap.get(`${SECRET_KEY_ACCOUNT_PREFIX}${TENANT_A}`),
      ).to.equal(JSON.stringify(accountA));
    });

    it("returns undefined when the account blob is corrupted", async () => {
      // Arrange
      const ctx = makeContext();
      ctx.secretsMap.set(`${SECRET_KEY_ACCOUNT_PREFIX}${TENANT_A}`, "{not-json");

      // Act
      const read = await readAccountAsync(ctx, TENANT_A);

      // Assert
      expect(read).to.equal(undefined);
    });
  });

  describe("deleteTenantSecretsAsync — FR-019 multi-tenant isolation", () => {
    it("removes tenant A secrets but leaves tenant B intact", async () => {
      // Arrange
      const ctx = makeContext();
      await persistTokenAsync(ctx, TENANT_A, "token-A");
      await persistAccountAsync(ctx, TENANT_A, accountA);
      await persistTokenAsync(ctx, TENANT_B, "token-B");

      // Act
      await deleteTenantSecretsAsync(ctx, TENANT_A);

      // Assert
      expect(await readTokenAsync(ctx, TENANT_A)).to.equal(undefined);
      expect(await readAccountAsync(ctx, TENANT_A)).to.equal(undefined);
      expect(await readTokenAsync(ctx, TENANT_B)).to.equal("token-B");
    });

    it("drops the tenant from the known-tenants index", async () => {
      // Arrange
      const ctx = makeContext();
      await persistTokenAsync(ctx, TENANT_A, "token-A");
      await persistTokenAsync(ctx, TENANT_B, "token-B");

      // Act
      await deleteTenantSecretsAsync(ctx, TENANT_A);

      // Assert
      expect(await listKnownTenantsAsync(ctx)).to.deep.equal([TENANT_B]);
    });
  });

  describe("active tenant id", () => {
    it("getActiveTenantId returns undefined when nothing is set", () => {
      // Arrange
      const ctx = makeContext();

      // Act + Assert
      expect(getActiveTenantId(ctx)).to.equal(undefined);
    });

    it("round-trips through setActiveTenantId", async () => {
      // Arrange
      const ctx = makeContext();

      // Act
      await setActiveTenantId(ctx, TENANT_A);

      // Assert
      expect(getActiveTenantId(ctx)).to.equal(TENANT_A);
      expect(ctx.stateMap.get(STATE_KEY_ACTIVE_TENANT)).to.equal(TENANT_A);
    });

    it("clears the active tenant id when set to undefined", async () => {
      // Arrange
      const ctx = makeContext();
      await setActiveTenantId(ctx, TENANT_A);

      // Act
      await setActiveTenantId(ctx, undefined);

      // Assert
      expect(getActiveTenantId(ctx)).to.equal(undefined);
      expect(ctx.stateMap.has(STATE_KEY_ACTIVE_TENANT)).to.equal(false);
    });
  });

  describe("listKnownTenantsAsync", () => {
    it("returns the union of all tenants for which a token was persisted", async () => {
      // Arrange
      const ctx = makeContext();

      // Act
      await persistTokenAsync(ctx, TENANT_A, "token-A");
      await persistTokenAsync(ctx, TENANT_B, "token-B");
      // Persisting the account for an already-known tenant must not duplicate.
      await persistAccountAsync(ctx, TENANT_A, accountA);
      const tenants = await listKnownTenantsAsync(ctx);

      // Assert — order preserved by insertion, no duplicates.
      expect(tenants).to.deep.equal([TENANT_A, TENANT_B]);
      // Sanity: the known-tenants index in workspaceState matches.
      expect(ctx.stateMap.get(STATE_KEY_KNOWN_TENANTS)).to.deep.equal([
        TENANT_A,
        TENANT_B,
      ]);
    });

    it("returns an empty array when the index is missing or malformed", async () => {
      // Arrange
      const ctx = makeContext();
      ctx.stateMap.set(STATE_KEY_KNOWN_TENANTS, "not-an-array");

      // Act
      const tenants = await listKnownTenantsAsync(ctx);

      // Assert
      expect(tenants).to.deep.equal([]);
    });
  });
});
