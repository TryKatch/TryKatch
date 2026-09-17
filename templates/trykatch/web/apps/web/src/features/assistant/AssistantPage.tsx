import { useContext } from 'react'
import { PageHeader, Surface } from '@trykatch/ui'
import { useI18n } from '../../i18n/I18nProvider'
import { AssistantChat, AssistantChatContext, AssistantChatProvider } from './AssistantChat'

export function AssistantPage() {
  const chat = useContext(AssistantChatContext)
  const { t } = useI18n()
  if (!chat) return <AssistantChatProvider><AssistantPage /></AssistantChatProvider>
  return <><PageHeader title={t('AI Help')} description={t('A conversation about your workspace.')} />{!chat.panelOpen && <Surface className="help-chat-page"><AssistantChat /></Surface>}</>
}
