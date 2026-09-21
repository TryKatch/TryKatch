import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'

test('landing page exposes persistent light, system, and dark modes', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light' })
  await page.goto('/')
  await page.getByRole('button', { name: 'Theme: System' }).click()

  const light = page.getByRole('menuitemradio', { name: 'Light' })
  const system = page.getByRole('menuitemradio', { name: 'System' })
  const dark = page.getByRole('menuitemradio', { name: 'Dark' })
  await expect(light).toBeVisible()
  await expect(system).toBeVisible()
  await expect(dark).toBeVisible()

  await dark.click()
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  await page.reload()
  await page.getByRole('button', { name: 'Theme: Dark' }).click()
  await expect(dark).toHaveAttribute('aria-checked', 'true')

  await system.click()
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light')
  await page.emulateMedia({ colorScheme: 'dark' })
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')

  await page.getByRole('button', { name: 'Language: English' }).click()
  await page.getByRole('menuitemradio', { name: 'French' }).click()
  await expect(page.locator('html')).toHaveAttribute('lang', 'fr')

  const results = await new AxeBuilder({ page }).analyze()
  expect(results.violations).toEqual([])
})
