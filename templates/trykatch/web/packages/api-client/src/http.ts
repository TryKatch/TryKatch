let antiforgeryToken: string | undefined

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
    throw Object.assign(new Error(problem.detail ?? problem.title ?? 'Request failed'), { status: response.status, problem })
  }

  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}
