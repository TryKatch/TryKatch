import type { DataTableLabels } from '@trykatch/ui'
import { useI18n } from './I18nProvider'

export function useDataTableLabels(): DataTableLabels {
  const { t } = useI18n()
  return {
    searchTable: t('Search table'),
    result: t('result'),
    results: t('results'),
    columns: t('Columns'),
    tableSettings: t('Table settings'),
    closeTableSettings: t('Close table settings'),
    rowDensity: t('Row density'),
    compact: t('Compact'),
    comfortable: t('Comfortable'),
    spacious: t('Spacious'),
    required: t('Required'),
    details: t('Details'),
    showDetails: (row) => t('Show details for {row}', { row }),
    hideDetails: (row) => t('Hide details for {row}', { row }),
    noMatchingResults: t('No matching results.'),
    showing: (start, end, total) => t('Showing {start}–{end} of {total}', { start, end, total }),
    previous: t('Previous'),
    page: (page, count) => t('Page {page} of {count}', { page, count }),
    next: t('Next'),
  }
}
