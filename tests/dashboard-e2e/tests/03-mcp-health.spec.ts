import { test, expect } from '@playwright/test';

/**
 * Smoke test #03 — MCP server health check.
 * Verifies that the ato-copilot MCP server responds on port 3001.
 */
test('MCP server /health returns 200', async ({ request }) => {
  const response = await request.get('http://localhost:3001/health');
  expect(response.status()).toBe(200);
});
