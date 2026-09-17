import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen } from '@testing-library/react'
import { assistantGuide } from '@trykatch/api-client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AssistantGuidePage } from './AssistantGuidePage'

vi.mock('@trykatch/api-client', () => ({ assistantGuide: vi.fn() }))
vi.mock('@tanstack/react-router', () => ({ useParams: () => ({ guideId: 'architecture' }), Link: ({ to, children }: { to: string; children: React.ReactNode }) => <a href={to}>{children}</a> }))
beforeEach(() => vi.clearAllMocks())
afterEach(cleanup)
function mount() {
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><AssistantGuidePage /></QueryClientProvider>)
}
describe('AssistantGuidePage', () => {
  it('uses the generated client and renders approved sections as safe readable text', async () => {
    vi.mocked(assistantGuide).mockResolvedValue({ id: 'architecture', title: 'Architecture overview', revision: 'A'.repeat(64), sections: [{ heading: 'Layers', text: 'Domain owns invariants.\n\n<script>not executable</script>' }] })
    const { container } = mount()
    expect(await screen.findByRole('heading', { name: 'Architecture overview' })).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Layers' })).toBeInTheDocument()
    expect(screen.getByText('Domain owns invariants.')).toBeInTheDocument()
    expect(screen.getByText('<script>not executable</script>')).toBeInTheDocument()
    expect(container.querySelector('script')).toBeNull()
    expect(assistantGuide).toHaveBeenCalledWith('architecture', expect.objectContaining({ signal: expect.any(AbortSignal) }))
    expect(screen.getByRole('link', { name: 'Back to AI Help' })).toHaveAttribute('href', '/assistant')
  })
  it('shows an unavailable guide without automatically retrying', async () => {
    vi.mocked(assistantGuide).mockRejectedValue(Object.assign(new Error('not found'), { status: 404 }))
    mount()
    expect(await screen.findByRole('alert')).toHaveTextContent(/guide is unavailable/)
    expect(assistantGuide).toHaveBeenCalledTimes(1)
  })
})
