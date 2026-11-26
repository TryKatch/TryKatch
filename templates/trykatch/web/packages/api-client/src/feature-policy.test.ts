import { afterEach, expect, it, vi } from 'vitest'
import { customFetch, subscribeFeaturePolicyRejections } from './http'

const subscriptions: (() => void)[] = []
afterEach(() => {
  subscriptions.splice(0).forEach(unsubscribe => unsubscribe())
  vi.unstubAllGlobals()
})

function respond(status: number, problem: object) {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(problem), {
    status, headers: { 'Content-Type': 'application/problem+json' },
  })))
}

it.each([
  [409, { title: 'feature_unavailable' }, 'feature_unavailable'],
  [503, { title: 'Policy failure', code: 'feature_policy_unavailable' }, 'feature_policy_unavailable'],
] as const)('notifies the host of a feature policy rejection while retaining the HTTP error (%s)', async (status, problem, code) => {
  const listener = vi.fn()
  subscriptions.push(subscribeFeaturePolicyRejections(listener))
  respond(status, problem)
  await expect(customFetch('/api/v1/projects', { method: 'GET' })).rejects.toMatchObject({ status, problem })
  expect(listener).toHaveBeenCalledExactlyOnceWith({ url: '/api/v1/projects', code })
})

it.each([
  [403, { title: 'forbidden' }],
  [409, { title: 'feature_control_conflict' }],
  [503, { title: 'provider_unavailable' }],
  [403, { title: 'feature_unavailable' }],
])('leaves unrelated and mismatched failures to their existing boundary (%s)', async (status, problem) => {
  const listener = vi.fn()
  subscriptions.push(subscribeFeaturePolicyRejections(listener))
  respond(status, problem)
  await expect(customFetch('/api/v1/projects', { method: 'GET' })).rejects.toMatchObject({ status })
  expect(listener).not.toHaveBeenCalled()
})

it('unsubscribes observers and preserves the original response even if another observer fails', async () => {
  const removed = vi.fn()
  const unsubscribe = subscribeFeaturePolicyRejections(removed)
  unsubscribe()
  unsubscribe()
  const active = vi.fn()
  subscriptions.push(subscribeFeaturePolicyRejections(() => { throw new Error('observer failed') }))
  subscriptions.push(subscribeFeaturePolicyRejections(active))
  respond(409, { title: 'feature_unavailable' })
  await expect(customFetch('/api/v1/projects', { method: 'GET' })).rejects.toMatchObject({ status: 409 })
  expect(removed).not.toHaveBeenCalled()
  expect(active).toHaveBeenCalledOnce()
})
