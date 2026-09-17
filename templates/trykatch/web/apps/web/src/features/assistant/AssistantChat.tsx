import { useQuery } from '@tanstack/react-query'
import { assistantAsk, assistantStatus, workspaceCurrent, type AssistantGuideSource } from '@trykatch/api-client'
import { Badge, Button } from '@trykatch/ui'
import { ArrowUp, BookOpen, Bot, FileText, FolderKanban, LoaderCircle, RotateCcw } from 'lucide-react'
import { createContext, useContext, useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { useI18n } from '../../i18n/I18nProvider'

interface ChatMessage { id: number; role: 'user' | 'assistant'; text: string; tools?: string[]; guides?: AssistantGuideSource[] }

function useChat() {
  const [message, setMessage] = useState('')
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [conversationToken, setConversationToken] = useState<string>()
  const [error, setError] = useState<'request' | 'conversation' | 'limit'>()
  const [pending, setPending] = useState(false)
  const active = useRef<{ controller: AbortController; question: string; id: number } | null>(null)
  const sequence = useRef(0)
  const workspace = useQuery({ queryKey: ['assistant-workspace'], queryFn: ({ signal }) => workspaceCurrent({ signal }), retry: false })
  const scope = `${workspace.data?.organizationId ?? ''}:${workspace.data?.membershipId ?? ''}:${workspace.data?.permissions.join(',') ?? ''}`
  const status = useQuery({ queryKey: ['assistant-status', scope], queryFn: ({ signal }) => assistantStatus({ signal }), enabled: workspace.isSuccess, retry: false })
  const available = Boolean(status.data?.enabled && (status.data.tools.length > 0 || status.data.helpAvailable) && workspace.isSuccess)

  function reset(preserveQuestion = false) {
    active.current?.controller.abort()
    active.current = null
    if (!preserveQuestion) setMessage('')
    setMessages([]); setConversationToken(undefined); setError(undefined); setPending(false)
  }
  useEffect(() => {
    active.current?.controller.abort(); active.current = null
    setMessage(''); setMessages([]); setConversationToken(undefined); setError(undefined); setPending(false)
    return () => { active.current?.controller.abort(); active.current = null }
  }, [scope])
  function cancel() {
    const request = active.current
    if (!request) return
    request.controller.abort(); active.current = null
    setMessages((items) => items.filter((item) => item.id !== request.id))
    setMessage(request.question); setPending(false)
  }
  async function send() {
    if (!available || !message.trim() || message.length > 2000 || active.current || error === 'conversation') return
    const request = { controller: new AbortController(), question: message, id: ++sequence.current }
    active.current = request
    setMessages((items) => [...items, { id: request.id, role: 'user', text: message.trim() }])
    setMessage(''); setPending(true); setError(undefined)
    try {
      const answer = await assistantAsk({ message: request.question.trim(), ...(conversationToken ? { conversationToken } : {}) }, { signal: request.controller.signal })
      if (active.current === request) {
        setConversationToken(answer.conversationToken ?? undefined)
        const response: ChatMessage = { id: ++sequence.current, role: 'assistant', text: answer.answer, tools: answer.toolsUsed, guides: answer.guides ?? undefined }
        setMessages((items) => [...items, response].slice(-40))
      }
    } catch (failure) {
      if (active.current === request && !request.controller.signal.aborted) {
        setMessages((items) => items.filter((item) => item.id !== request.id))
        setMessage(request.question)
        const problem = failure as { status?: number; problem?: { title?: string } }
        setError(problem?.status === 409 ? 'conversation' : problem?.problem?.title === 'response_limit' ? 'limit' : 'request')
      }
    } finally {
      if (active.current === request) { active.current = null; setPending(false) }
    }
  }
  return { message, setMessage, messages, error, pending, workspace, status, available, reset, cancel, send }
}

export const AssistantChatContext = createContext<(ReturnType<typeof useChat> & { panelOpen: boolean }) | null>(null)
export function AssistantChatProvider({ children, panelOpen = false }: { children: ReactNode; panelOpen?: boolean }) {
  const chat = useChat()
  return <AssistantChatContext.Provider value={{ ...chat, panelOpen }}>{children}</AssistantChatContext.Provider>
}

export function AssistantChat() {
  const chat = useContext(AssistantChatContext)
  const { t } = useI18n()
  const id = useId()
  const input = useRef<HTMLTextAreaElement>(null)
  const transcript = useRef<HTMLDivElement>(null)
  const wasPending = useRef(false)
  useEffect(() => {
    if (transcript.current) transcript.current.scrollTop = transcript.current.scrollHeight
    if (wasPending.current && !chat?.pending) input.current?.focus()
    wasPending.current = Boolean(chat?.pending)
  }, [chat?.messages, chat?.pending, chat?.error])
  if (!chat) throw new Error('Assistant chat requires its provider.')
  const suggestions = [
    { tool: 'help', label: 'Get started', question: 'How do I start using this application? Explain the main features and my next steps.', icon: BookOpen },
    { tool: 'help', label: 'Understand the architecture', question: 'Explain this application’s architecture and module layers.', icon: BookOpen },
    { tool: 'list_projects', label: 'Explore projects', question: 'Show me the projects in this workspace.', icon: FolderKanban },
    { tool: 'list_documents', label: 'Find documents', question: 'Help me find documents in this workspace.', icon: FileText },
  ].filter(({ tool }) => tool === 'help' ? chat.status.data?.helpAvailable : chat.status.data?.tools.some((name) => name === tool || name.endsWith(`_${tool}`)))
  const sources = (tools: string[]) => [...new Set(tools.map((tool) => tool.includes('project') ? t('Projects') : tool.includes('document') ? t('Documents') : t('Workspace data')))]
  const safeGuides = (guides: AssistantGuideSource[]) => guides.filter((guide) => /^[a-z][a-z0-9-]{0,79}$/.test(guide.id) && guide.href === `/assistant/guides/${guide.id}`)

  return <div className="help-chat">
    <header className="help-chat-toolbar"><Badge>{t('Read-only')}</Badge><Button type="button" variant="ghost" onClick={() => { chat.reset(chat.error === 'conversation'); input.current?.focus() }}><RotateCcw size={14} aria-hidden="true" />{t('New conversation')}</Button></header>
    <div ref={transcript} className="help-chat-transcript" role="log" aria-label={t('Conversation')} aria-live="polite" aria-relevant="additions text">
      {chat.messages.length === 0 && <div className="help-chat-welcome"><span className="help-chat-avatar"><Bot size={24} aria-hidden="true" /></span><h2>{t('How can I help?')}</h2><p>{t(chat.status.data?.helpAvailable ? 'Ask how this application works, how to use a feature, or what to do next.' : 'Ask about projects and documents in your workspace, then ask a follow-up.')}</p>
        {chat.available && suggestions.length > 0 && <div className="assistant-suggestions" aria-label={t('Suggested questions')}>{suggestions.map(({ label, question, icon: Icon }) => <button key={label} type="button" disabled={chat.pending} onClick={() => { chat.setMessage(t(question)); input.current?.focus() }}><Icon size={16} aria-hidden="true" /><span>{t(label)}</span></button>)}</div>}
      </div>}
      {chat.messages.map((item) => <div key={item.id} className={`help-chat-message is-${item.role}`}><small>{t(item.role === 'user' ? 'You' : 'AI Help')}</small><p>{item.text}</p>{item.role === 'assistant' && Boolean(item.tools?.length) && <details className="help-chat-sources"><summary>{t('Sources')}</summary><small>{sources(item.tools!).join(', ')}</small></details>}{item.role === 'assistant' && safeGuides(item.guides ?? []).length > 0 && <details className="help-chat-sources"><summary>{t('Guides consulted')}</summary>{safeGuides(item.guides!).map((guide) => <a key={guide.id} href={guide.href} target="_blank" rel="noopener noreferrer">{t(guide.title)}<span className="sr-only"> {t('(opens in a new tab)')}</span></a>)}</details>}</div>)}
      {chat.pending && <div className="help-chat-thinking" role="status"><LoaderCircle size={15} className="assistant-spinner" aria-hidden="true" />{t('Thinking…')}</div>}
    </div>
    {chat.status.isPending && !chat.workspace.isError && <p className="assistant-notice" role="status">{t('Loading…')}</p>}
    {(chat.status.isError || chat.workspace.isError) && <p className="assistant-notice" role="alert">{t('Assistant availability could not be checked.')}</p>}
    {chat.status.data && !chat.status.data.enabled && <p className="assistant-notice" role="status">{t('The assistant is disabled. Ask your administrator to configure it.')}</p>}
    {chat.status.data?.enabled && chat.status.data.tools.length === 0 && !chat.status.data.helpAvailable && <p className="assistant-notice" role="status">{t('No assistant tools are available for your permissions.')}</p>}
    {chat.error && <p className="assistant-notice" role="alert">{t(chat.error === 'conversation' ? 'This conversation expired or your access changed. Start a new conversation. Your question is preserved.' : chat.error === 'limit' ? 'The answer exceeded the response limit. Ask a more focused question. Your question is preserved; no changes were made.' : 'The assistant could not answer. Your question is preserved; try again. No changes were made.')}</p>}
    <form className="help-chat-composer" onSubmit={(event) => { event.preventDefault(); void chat.send() }}>
      <label className="sr-only" htmlFor={`${id}-question`}>{t('Your question')}</label>
      <div className="help-chat-input"><textarea ref={input} id={`${id}-question`} placeholder={t('Type a message…')} aria-describedby={`${id}-hint`} maxLength={2000} rows={2} value={chat.message} disabled={chat.pending || !chat.available} onChange={(event) => chat.setMessage(event.target.value)} onKeyDown={(event) => {
        if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) { event.preventDefault(); if (!event.repeat) event.currentTarget.form?.requestSubmit() }
      }} /><Button type="submit" variant="primary" aria-label={t('Ask assistant')} title={t('Ask assistant')} disabled={!chat.available || chat.pending || !chat.message.trim() || chat.error === 'conversation'}><ArrowUp size={18} aria-hidden="true" /></Button></div>
      <div className="help-chat-input-hint"><small id={`${id}-hint`}>{t('Enter to send · Shift+Enter for a new line')}</small><small>{chat.message.length.toLocaleString()} / 2,000</small>{chat.pending && <Button type="button" variant="ghost" onClick={chat.cancel}>{t('Cancel')}</Button>}</div>
    </form>
    <aside className="help-chat-privacy" aria-label={t('About this assistant')}><details><summary>{t('Privacy & limitations')}</summary><p>{t('Questions, recent chat context, approved help excerpts and retrieved workspace data are sent to the configured AI provider. No files are uploaded. Verify important details.')}</p><p>{t('Up to four recent exchanges provide follow-up context for up to 20 minutes. Refresh or start a new conversation to clear this chat.')}</p><p>{t(chat.status.data?.helpAvailable ? 'Guidance uses curated documentation and enabled module declarations, not an inspection of your custom source code.' : 'This assistant can read authorized workspace data, not project source code or architecture documentation.')}</p></details></aside>
  </div>
}
