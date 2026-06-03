import { test, expect } from '@playwright/test';

/**
 * Smoke test #04 — Conversation creation via REST API.
 * Verifies that POST /api/conversations returns HTTP 201 with a conversation ID.
 */
test('POST /api/conversations returns 201', async ({ request }) => {
  const response = await request.post('http://localhost:5001/api/conversations', {
    data: { title: 'Smoke test conversation' },
  });
  expect(response.status()).toBe(201);
  const body = await response.json();
  expect(body).toHaveProperty('id');
});
