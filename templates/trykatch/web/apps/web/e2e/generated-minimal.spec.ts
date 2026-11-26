import { expect, test } from '@playwright/test'

const acceptanceBaseUrl = process.env.TRYKATCH_ACCEPTANCE_BASE_URL
const platformAdminEmail = process.env.TRYKATCH_ACCEPTANCE_ADMIN_EMAIL
const platformAdminPassword = process.env.TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD

test.describe('generated minimal application', () => {
  test.skip(!acceptanceBaseUrl, 'Runs against a minimal generated application started by the acceptance harness.')

  test('starts with no example module routes and allows administrator sign-in', async ({ page }) => {
    expect(platformAdminEmail, 'TRYKATCH_ACCEPTANCE_ADMIN_EMAIL').toBeTruthy()
    expect(platformAdminPassword, 'TRYKATCH_ACCEPTANCE_ADMIN_PASSWORD').toBeTruthy()

    await page.goto('/login')
    await page.getByLabel('Email address').fill(platformAdminEmail!)
    await page.locator('input[name="password"]').fill(platformAdminPassword!)
    await page.getByRole('button', { name: 'Sign in', exact: true }).click()
    await expect(page).toHaveURL(/\/dashboard$/)
    await expect(page.getByRole('heading', { name: 'Platform overview' })).toBeVisible()

    for (const route of ['/api/v1/projects', '/api/v1/documents']) {
      const response = await page.context().request.get(route)
      expect(response.status(), `${route} should not be registered`).toBe(404)
    }
  })
})
