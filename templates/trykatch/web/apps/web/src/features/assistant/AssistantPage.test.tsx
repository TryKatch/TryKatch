import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { assistantAsk, assistantStatus, workspaceCurrent } from '@trykatch/api-client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AssistantPage } from './AssistantPage'

vi.mock('@trykatch/api-client', () => ({ assistantAsk: vi.fn(), assistantStatus: vi.fn(), workspaceCurrent: vi.fn() }))

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return { client, ...render(<QueryClientProvider client={client}><AssistantPage /></QueryClientProvider>) }
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(workspaceCurrent).mockResolvedValue({ organizationId: 'org-a', membershipId: 'member-a', permissions: ['projects.read'] })
  vi.mocked(assistantStatus).mockResolvedValue({ enabled: true, readOnly: true, tools: ['list_projects'] })
})
afterEach(cleanup)

describe('AssistantPage', () => {
  it('distinguishes response limits and preserves the question without automatically retrying', async () => {
    vi.mocked(assistantAsk).mockRejectedValue(Object.assign(new Error('limited'), { status: 502, problem: { title: 'response_limit' } }))
    mount()
    const input = screen.getByLabelText('Your question')
    await waitFor(() => expect(input).toBeEnabled())
    fireEvent.change(input, { target: { value: 'Explain that further, with examples.' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(await screen.findByRole('alert')).toHaveTextContent('The answer exceeded the response limit. Ask a more focused question. Your question is preserved; no changes were made.')
    expect(input).toHaveValue('Explain that further, with examples.')
    expect(assistantAsk).toHaveBeenCalledTimes(1)
    expect(input).toBeEnabled()
  })
  it('offers only authorized starters and fills the composer without submitting', async () => {
    vi.mocked(assistantStatus).mockResolvedValue({ enabled: true, readOnly: true, tools: ['trykatch_list_projects'] })
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Explore projects' }))
    expect(screen.queryByRole('button', { name: 'Find documents' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Your question')).toHaveValue('Show me the projects in this workspace.')
    expect(screen.getByLabelText('Your question')).toHaveFocus()
    expect(assistantAsk).not.toHaveBeenCalled()
  })

  it('keeps the input limit and privacy guidance visible', async () => {
    mount()
    await waitFor(() => expect(screen.getByLabelText('Your question')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Your question'), { target: { value: 'Hello' } })
    expect(screen.getByLabelText('Your question')).toHaveAttribute('maxlength', '2000')
    expect(screen.getByLabelText('Your question').closest('.floating-control')).not.toBeNull()
    expect(screen.getByText('5 / 2,000')).toBeInTheDocument()
    expect(screen.getByRole('complementary', { name: 'About this assistant' })).toHaveTextContent(/configured AI provider/)
  })

  it('shows configuration-disabled state without calling a model', async () => {
    vi.mocked(assistantStatus).mockResolvedValue({ enabled: false, readOnly: true, tools: [] })
    mount()
    expect(await screen.findByText(/assistant is disabled/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Ask assistant' })).toBeDisabled()
    expect(assistantAsk).not.toHaveBeenCalled()
  })

  it('renders plain text and server-returned tool provenance', async () => {
    vi.mocked(assistantAsk).mockResolvedValue({ answer: '<script>not executable</script> One project.', toolsUsed: ['list_projects'] })
    const { container } = mount()
    await waitFor(() => expect(screen.getByLabelText('Your question')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Your question'), { target: { value: 'List projects' } })
    fireEvent.click(screen.getByRole('button', { name: 'Ask assistant' }))
    expect(await screen.findByText(/One project/)).toBeInTheDocument()
    expect(container.querySelector('script')).toBeNull()
    expect(screen.getByText('Sources')).toBeInTheDocument()
    expect(screen.getByText('Projects')).toBeInTheDocument()
    expect(screen.getByLabelText('Your question')).toHaveValue('')
    expect(assistantAsk).toHaveBeenCalledWith({ message: 'List projects' }, expect.objectContaining({ signal: expect.any(AbortSignal) }))
  })

  it('Enter submits the captured question once and clears the input immediately', async () => {
    vi.mocked(assistantAsk).mockReturnValue(new Promise(() => {}))
    mount()
    const input = screen.getByLabelText('Your question')
    await waitFor(() => expect(input).toBeEnabled())
    fireEvent.change(input, { target: { value: '  List projects  ' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(input).toHaveValue('')
    expect(input).toBeDisabled()
    expect(assistantAsk).toHaveBeenCalledExactlyOnceWith({ message: 'List projects' }, expect.objectContaining({ signal: expect.any(AbortSignal) }))
    fireEvent.keyDown(input, { key: 'Enter' })
    fireEvent.submit(input.closest('form')!)
    expect(assistantAsk).toHaveBeenCalledTimes(1)
  })

  it('Shift+Enter, composing Enter, and held Enter do not submit', async () => {
    mount()
    const input = screen.getByLabelText('Your question')
    await waitFor(() => expect(input).toBeEnabled())
    fireEvent.change(input, { target: { value: 'My question' } })
    expect(fireEvent.keyDown(input, { key: 'Enter', shiftKey: true })).toBe(true)
    expect(fireEvent.keyDown(input, { key: 'Enter', isComposing: true })).toBe(true)
    expect(fireEvent.keyDown(input, { key: 'Enter', keyCode: 229 })).toBe(true)
    fireEvent.keyDown(input, { key: 'Enter', repeat: true })
    expect(assistantAsk).not.toHaveBeenCalled()
    expect(input).toHaveValue('My question')
  })

  it('does not submit blank questions or bypass unavailable tools with Enter', async () => {
    vi.mocked(assistantStatus).mockResolvedValue({ enabled: true, readOnly: true, tools: [] })
    mount()
    await screen.findByText(/No assistant tools/)
    const input = screen.getByLabelText('Your question')
    fireEvent.change(input, { target: { value: 'My question' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(assistantAsk).not.toHaveBeenCalled()
  })

  it('does not submit whitespace-only questions with Enter', async () => {
    mount()
    const input = screen.getByLabelText('Your question')
    await waitFor(() => expect(input).toBeEnabled())
    fireEvent.change(input, { target: { value: '  \n ' } })
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(assistantAsk).not.toHaveBeenCalled()
    expect(input).toHaveValue('  \n ')
  })

  it('preserves the question on failure', async () => {
    vi.mocked(assistantAsk).mockRejectedValue(new Error('provider unavailable'))
    mount()
    await waitFor(() => expect(screen.getByLabelText('Your question')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Your question'), { target: { value: 'My question' } })
    fireEvent.click(screen.getByRole('button', { name: 'Ask assistant' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/question is preserved/i)
    expect(screen.getByLabelText('Your question')).toHaveValue('My question')
  })

  it('keeps the transcript and sends only the server continuation for follow-ups', async () => {
    vi.mocked(assistantAsk).mockResolvedValueOnce({ answer: 'First answer', toolsUsed: [], conversationToken: 'server-token' }).mockResolvedValueOnce({ answer: 'Follow-up answer', toolsUsed: [], conversationToken: 'next-token' })
    mount()
    const input = screen.getByLabelText('Your question')
    await waitFor(() => expect(input).toBeEnabled())
    fireEvent.change(input, { target: { value: 'First question' } }); fireEvent.keyDown(input, { key: 'Enter' })
    await screen.findByText('First answer')
    fireEvent.change(input, { target: { value: 'Explain that further' } }); fireEvent.keyDown(input, { key: 'Enter' })
    await screen.findByText('Follow-up answer')
    expect(screen.getByRole('log')).toHaveTextContent('First question')
    expect(screen.getByRole('log')).toHaveTextContent('First answer')
    expect(assistantAsk).toHaveBeenLastCalledWith({ message: 'Explain that further', conversationToken: 'server-token' }, expect.objectContaining({ signal: expect.any(AbortSignal) }))
    fireEvent.click(screen.getByRole('button', { name: 'New conversation' }))
    expect(screen.queryByText('First answer')).not.toBeInTheDocument()
    fireEvent.change(input, { target: { value: 'Fresh question' } }); fireEvent.keyDown(input, { key: 'Enter' })
    expect(assistantAsk).toHaveBeenLastCalledWith({ message: 'Fresh question' }, expect.objectContaining({ signal: expect.any(AbortSignal) }))
  })

  it('expired or changed-access context requires explicit reset, not an automatic retry', async () => {
    vi.mocked(assistantAsk).mockRejectedValue(Object.assign(new Error('expired'), { status: 409 }))
    mount()
    const input = screen.getByLabelText('Your question')
    await waitFor(() => expect(input).toBeEnabled())
    fireEvent.change(input, { target: { value: 'Follow up' } }); fireEvent.keyDown(input, { key: 'Enter' })
    expect(await screen.findByRole('alert')).toHaveTextContent(/conversation expired/)
    expect(input).toHaveValue('Follow up')
    expect(screen.getByRole('button', { name: 'Ask assistant' })).toBeDisabled()
    fireEvent.keyDown(input, { key: 'Enter' })
    expect(assistantAsk).toHaveBeenCalledTimes(1)
  })

  it('cancellation ignores a late reply and preserves input', async () => {
    let release: ((answer: { answer: string; toolsUsed: string[] }) => void) | undefined
    vi.mocked(assistantAsk).mockReturnValue(new Promise((resolve) => { release = resolve }))
    mount()
    await waitFor(() => expect(screen.getByLabelText('Your question')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Your question'), { target: { value: 'Question' } })
    fireEvent.click(screen.getByRole('button', { name: 'Ask assistant' }))
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    release?.({ answer: 'Late reply', toolsUsed: [] })
    await waitFor(() => expect(screen.getByLabelText('Your question')).toBeEnabled())
    expect(screen.queryByText('Late reply')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Your question')).toHaveValue('Question')
  })

  it('scope changes clear answers and cancel pending requests', async () => {
    let signal: AbortSignal | null | undefined
    vi.mocked(assistantAsk).mockImplementation((_, options) => { signal = options?.signal; return new Promise(() => {}) })
    const { client } = mount()
    await waitFor(() => expect(screen.getByLabelText('Your question')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Your question'), { target: { value: 'Org A' } })
    fireEvent.click(screen.getByRole('button', { name: 'Ask assistant' }))
    await act(async () => { client.setQueryData(['assistant-workspace'], { organizationId: 'org-b', membershipId: 'member-b', permissions: ['projects.read'] }) })
    await waitFor(() => expect(signal?.aborted).toBe(true))
    expect(screen.getByLabelText('Your question')).toHaveValue('')
  })

  it('scope changes discard completed history and its continuation', async () => {
    vi.mocked(assistantAsk).mockResolvedValue({ answer: 'Organization A answer', toolsUsed: [], conversationToken: 'organization-a-token' })
    const { client } = mount()
    const input = screen.getByLabelText('Your question')
    await waitFor(() => expect(input).toBeEnabled())
    fireEvent.change(input, { target: { value: 'Organization A question' } }); fireEvent.keyDown(input, { key: 'Enter' })
    await screen.findByText('Organization A answer')
    await act(async () => { client.setQueryData(['assistant-workspace'], { organizationId: 'org-b', membershipId: 'member-b', permissions: ['projects.read'] }) })
    await waitFor(() => expect(input).toBeEnabled())
    await waitFor(() => expect(screen.queryByText('Organization A answer')).not.toBeInTheDocument())
    fireEvent.change(input, { target: { value: 'Organization B question' } }); fireEvent.keyDown(input, { key: 'Enter' })
    expect(assistantAsk).toHaveBeenLastCalledWith({ message: 'Organization B question' }, expect.objectContaining({ signal: expect.any(AbortSignal) }))
  })

  it('offers user getting-started guidance without requiring record permissions or sending automatically', async () => {
    vi.mocked(assistantStatus).mockResolvedValue({ enabled: true, readOnly: true, tools: [], helpAvailable: true })
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Get started' }))
    expect(screen.getByLabelText('Your question')).toHaveValue('How do I start using this application? Explain the main features and my next steps.')
    expect(assistantAsk).not.toHaveBeenCalled()
    expect(screen.queryByRole('button', { name: 'Explore projects' })).not.toBeInTheDocument()
  })

  it('offers grounded architecture help without granting record starters', async () => {
    vi.mocked(assistantStatus).mockResolvedValue({ enabled: true, readOnly: true, tools: [], helpAvailable: true })
    vi.mocked(assistantAsk).mockResolvedValue({ answer: 'The approved guide describes Domain and Application layers.', toolsUsed: [], guides: [
      { id: 'architecture', title: 'Architecture overview', revision: 'A'.repeat(64), href: '/assistant/guides/architecture' },
      { id: 'forged', title: 'Unsafe source', revision: 'A'.repeat(64), href: 'javascript:alert(1)' },
    ] })
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Understand the architecture' }))
    expect(screen.queryByRole('button', { name: 'Explore projects' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Find documents' })).not.toBeInTheDocument()
    expect(screen.getByLabelText('Your question')).toHaveValue('Explain this application’s architecture and module layers.')
    fireEvent.keyDown(screen.getByLabelText('Your question'), { key: 'Enter' })
    await screen.findByText(/approved guide describes/)
    fireEvent.click(screen.getByText('Guides consulted'))
    expect(screen.getByRole('link', { name: /Architecture overview/ })).toHaveAttribute('href', '/assistant/guides/architecture')
    expect(screen.getByRole('link', { name: /Architecture overview/ })).toHaveAttribute('rel', 'noopener noreferrer')
    expect(screen.queryByText('Unsafe source')).not.toBeInTheDocument()
    expect(screen.queryByText(/No assistant tools/)).not.toBeInTheDocument()
  })

  it('starting a new conversation aborts pending work and ignores its late response', async () => {
    let release: ((answer: { answer: string; toolsUsed: string[]; conversationToken: string }) => void) | undefined
    let signal: AbortSignal | null | undefined
    vi.mocked(assistantAsk).mockImplementation((_, options) => {
      signal = options?.signal
      return new Promise((resolve) => { release = resolve })
    })
    mount()
    const input = screen.getByLabelText('Your question')
    await waitFor(() => expect(input).toBeEnabled())
    fireEvent.change(input, { target: { value: 'Old question' } }); fireEvent.keyDown(input, { key: 'Enter' })
    fireEvent.click(screen.getByRole('button', { name: 'New conversation' }))
    expect(signal?.aborted).toBe(true)
    await act(async () => { release?.({ answer: 'Old response', toolsUsed: [], conversationToken: 'old-token' }) })
    expect(screen.queryByText('Old response')).not.toBeInTheDocument()
    expect(input).toHaveValue('')
    expect(input).toBeEnabled()
    fireEvent.change(input, { target: { value: 'Fresh question' } }); fireEvent.keyDown(input, { key: 'Enter' })
    expect(assistantAsk).toHaveBeenLastCalledWith({ message: 'Fresh question' }, expect.objectContaining({ signal: expect.any(AbortSignal) }))
  })
})
