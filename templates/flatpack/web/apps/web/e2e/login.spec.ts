import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'

test('login surface is keyboard-ready and accessible', async ({ page }) => {
  await page.goto('/login')
  await expect(page.getByRole('heading', { name: 'Sign in to your workspace' })).toBeVisible()
  await page.getByLabel('Email address').focus()
  await expect(page.getByLabel('Email address')).toBeFocused()
  const results = await new AxeBuilder({ page }).analyze()
  expect(results.violations).toEqual([])
})
