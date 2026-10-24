let antiforgeryToken: string | undefined

export interface FeaturePolicyRejection {
  url: string
  code: 'feature_unavailable' | 'feature_policy_unavailable'
}

const featurePolicyListeners = new Set<(rejection: FeaturePolicyRejection) => void>()

export function subscribeFeaturePolicyRejections(listener: (rejection: FeaturePolicyRejection) => void): () => void {
  featurePolicyListeners.add(listener)
  return () => { featurePolicyListeners.delete(listener) }
}

export function setAntiforgeryToken(token: string) {
  antiforgeryToken = token
}

export async function customFetch<T>(url: string, options: RequestInit): Promise<T> {
  const method = options.method ?? 'GET'
  if (!antiforgeryToken && !['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    const tokenResponse = await fetch('/api/v1/auth/antiforgery', { credentials: 'include' })
    if (!tokenResponse.ok) throw new Error('Unable to initialize request protection')
    const tokens = await tokenResponse.json() as { token: string }
    antiforgeryToken = tokens.token
  }

  const headers = new Headers(options.headers)
  if (antiforgeryToken && !['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    headers.set('X-CSRF-TOKEN', antiforgeryToken)
  }

  const response = await fetch(url, { ...options, headers, credentials: 'include' })
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ title: response.statusText }))
    const code = problem.code ?? problem.title
    if (response.status === 409 && code === 'feature_unavailable'
      || response.status === 503 && code === 'feature_policy_unavailable') {
      for (const listener of featurePolicyListeners) {
        try { listener({ url, code }) }
        catch { /* A subscriber must not replace the original HTTP failure. */ }
      }
    }
    throw Object.assign(new Error(problem.detail ?? problem.title ?? 'Request failed'), { status: response.status, problem })
  }

  if (response.status === 204) return undefined as T
  const contentType = response.headers.get('Content-Type')?.toLowerCase() ?? ''
  if (contentType.includes('application/json') || contentType.includes('+json')) {
    return response.json() as Promise<T>
  }
  if (contentType.startsWith('text/')) return response.text() as Promise<T>
  return response.blob() as Promise<T>
}
