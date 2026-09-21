import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'

test('login surface is keyboard-ready and accessible', async ({ page }) => {
  await page.goto('/login')
  await expect(page.getByRole('heading', { name: 'Sign in', exact: true })).toBeVisible()
  await page.getByLabel('Email address').focus()
  await expect(page.getByLabel('Email address')).toBeFocused()
  const results = await new AxeBuilder({ page }).analyze()
  expect(results.violations).toEqual([])
})

test('password recovery uses a dedicated accessible page', async ({ page }) => {
  await page.goto('/login')
  await page.getByRole('link', { name: 'Forgot password?' }).click()
  await expect(page).toHaveURL('/forgot-password')
  await expect(page.getByRole('heading', { name: 'Forgot your password?' })).toBeVisible()
  await expect(page.getByLabel('Email address')).toBeFocused()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  const results = await new AxeBuilder({ page }).analyze()
  expect(results.violations).toEqual([])
})
