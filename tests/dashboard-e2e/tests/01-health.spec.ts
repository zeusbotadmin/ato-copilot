import { test, expect } from '@playwright/test';

/**
 * Smoke test #01 — Chat app health check.
 * Verifies that the ato-chat service responds with HTTP 200 on /health.
 */
test('GET /health returns 200', async ({ request }) => {
  const response = await request.get('http://localhost:5001/health');
  expect(response.status()).toBe(200);
});
