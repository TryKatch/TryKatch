import type { DeleteConfirmationLabels } from '@trykatchapp/ui'
import { useI18n } from './I18nProvider'

export function useDeleteConfirmationLabels(): DeleteConfirmationLabels {
  const { t } = useI18n()
  return {
    title: (recordType) => t('Delete {recordType}', { recordType }),
    description: t('Request deletion while keeping the record recoverable in Archive.'),
    reasonLabel: t('Reason for deletion'),
    reasonPlaceholder: t('Explain why this record is being deleted…'),
    characterCount: (count) => t('{count}/500 · minimum 10 characters', { count }),
    accountability: t('The record moves to Pending deletion. The reason is stored with it and written to the immutable audit trail; authorized users can still restore it.'),
    cancel: t('Cancel'),
    requesting: t('Requesting…'),
    requestDeletion: t('Request deletion'),
  }
}
