import { defineConfig } from '@playwright/test';

/**
 * Playwright config for ATO Copilot dashboard E2E smoke tests.
 *
 * Targets:
 *   - ato-chat (dashboard UI): http://localhost:5001
 *   - ato-copilot (MCP server): http://localhost:3001
 *
 * Full E2E requires the docker-compose stack:
 *   docker compose -f docker-compose.mcp.yml up -d --wait
 *
 * For CI without Azure secrets, the job is currently disabled (if: false).
 * Enable it once environment secrets are wired up.
 */
export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  retries: 1,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:5001',
    extraHTTPHeaders: {
      'Accept': 'application/json',
    },
  },
});
