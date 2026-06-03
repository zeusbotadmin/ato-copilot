import { test, expect } from '@playwright/test';

/**
 * Smoke test #02 — Chat page loads and has an input element.
 * Verifies that the ato-chat dashboard UI renders and contains a chat input.
 */
test('chat page loads and has input', async ({ page }) => {
  await page.goto('http://localhost:5001');
  await expect(page).toHaveTitle(/.+/);
  // The chat UI should expose some kind of text input
  const input = page.locator('input[type="text"], textarea, [role="textbox"]').first();
  await expect(input).toBeVisible({ timeout: 10_000 });
});
