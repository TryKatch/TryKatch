import { expect, test, type Page } from '@playwright/test'

const work = { workId: 'work-1', organizationId: 'org-1', jobKey: 'billing.reconcile', jobVersion: 1, state: 'queued', attempts: 0, operatorPaused: false, recoveryGeneration: 2, createdAtUtc: '2026-10-23T10:00:00Z', dueAtUtc: null, nextAttemptAtUtc: null, failureCode: null }

async function mockPlatformWork(page: Page, permissions: string[], actionStatus = 200, action: 'pause' | 'resume' | 'retry' = 'pause', paginate = false) {
  let paused = false
  const initialWork = action === 'resume' ? { ...work, state: 'paused', operatorPaused: true } : action === 'retry' ? { ...work, state: 'terminal' } : work
  await page.route('**/api/v1/auth/session', (route) => route.fulfill({ json: { displayName: 'Operator', email: 'operator@example.test', hasPlatformAccess: true, platformPermissions: permissions } }))
  await page.route('**/api/v1/auth/antiforgery', (route) => route.fulfill({ json: { token: 'playwright-only' } }))
  await page.route('**/api/v1/platform/module-work**', async (route) => {
    if (route.request().method() === 'GET') {
      const params = new URL(route.request().url()).searchParams
      if (params.get('state') === 'denied') expect(params.get('page')).toBe('1')
      const items = params.get('state') === 'denied' ? [{ ...work, state: 'denied' }] : [{ ...initialWork, operatorPaused: paused ? action === 'pause' : initialWork.operatorPaused, recoveryGeneration: paused ? 3 : 2 }]
      return route.fulfill({ json: { items, page: Number(params.get('page') ?? 1), pageSize: 50, totalCount: params.get('state') === 'denied' ? 1 : paginate ? 51 : 1 } })
    }
    if (actionStatus !== 200) return route.fulfill({ status: actionStatus, json: { title: actionStatus === 409 ? 'Conflict' : 'Unavailable' } })
    const body = route.request().postDataJSON() as { expectedGeneration: number; requestId: string }
    expect(body.expectedGeneration).toBe(2)
    expect(body.requestId).toBeTruthy()
    expect(new URL(route.request().url()).pathname).toBe(`/api/v1/platform/module-work/${work.workId}/${action}`)
    paused = action === 'pause'
    return route.fulfill({ json: { workId: work.workId, action, recoveryGeneration: 3, operatorPaused: paused } })
  })
}

test('operators can confirm work actions from a readable desktop list', async ({ page }) => {
  await mockPlatformWork(page, ['platform.jobs.read', 'platform.jobs.manage'])
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/dashboard/module-work')
  await expect(page.getByRole('heading', { name: 'Platform work' })).toBeVisible()
  await expect(page.getByText('Immediate')).toBeVisible()
  await page.getByRole('button', { name: 'Actions for billing.reconcile' }).click()
  await page.getByRole('menuitem', { name: 'Pause' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Pause this work item?')
  await dialog.getByRole('button', { name: 'Confirm pause' }).click()
  await expect(page.getByRole('status')).toContainText('Work item paused.')
  await expect(page.getByText('Paused by operator')).toBeVisible()
})

test('status filter uses the selected state and resets pagination', async ({ page }) => {
  await mockPlatformWork(page, ['platform.jobs.read'], 200, 'pause', true)
  await page.goto('/dashboard/module-work')
  await page.getByRole('button', { name: 'Next' }).click()
  await page.getByRole('combobox', { name: 'Filter by status' }).click()
  await page.getByRole('option', { name: 'denied' }).click()
  await expect(page.getByRole('combobox', { name: 'Filter by status' })).toContainText('denied')
  await expect(page.getByText('Page 1 of 1')).toBeVisible()
  await expect(page.getByRole('table', { name: 'Platform work' }).getByText('denied')).toBeVisible()
})

test('operators can confirm resume and retry using the current recovery generation', async ({ page }) => {
  for (const scenario of [
    { action: 'resume' as const, menuItem: 'Resume', confirmation: 'Confirm resume', dialogText: 'Resume this work item?', success: 'Work item resumed.' },
    { action: 'retry' as const, menuItem: 'Retry', confirmation: 'Confirm retry', dialogText: 'Queue this work item for another attempt?', success: 'Work item queued for retry.' },
  ]) {
    await mockPlatformWork(page, ['platform.jobs.read', 'platform.jobs.manage'], 200, scenario.action)
    await page.goto('/dashboard/module-work')
    await expect(page.getByText('billing.reconcile')).toBeVisible()
    await page.getByRole('button', { name: 'Actions for billing.reconcile' }).click()
    await page.getByRole('menuitem', { name: scenario.menuItem }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog).toContainText(scenario.dialogText)
    await dialog.getByRole('button', { name: scenario.confirmation }).click()
    await expect(page.getByRole('status')).toContainText(scenario.success)
  }
})

test('read-only platform access stays readable on a phone viewport', async ({ page }) => {
  await mockPlatformWork(page, ['platform.jobs.read'])
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/dashboard/module-work')
  await expect(page.getByRole('heading', { name: 'Platform work' })).toBeVisible()
  await expect(page.getByText('Read-only access')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Actions for billing.reconcile' })).toHaveCount(0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})

test('stale work generation keeps the row visible and reports a conflict', async ({ page }) => {
  await mockPlatformWork(page, ['platform.jobs.read', 'platform.jobs.manage'], 409)
  await page.goto('/dashboard/module-work')
  await page.getByRole('button', { name: 'Actions for billing.reconcile' }).click()
  await page.getByRole('menuitem', { name: 'Pause' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm pause' }).click()
  await expect(page.getByRole('alert')).toContainText('Work item changed. Reload the list before trying again.')
  await expect(page.getByText('billing.reconcile')).toBeVisible()
})
