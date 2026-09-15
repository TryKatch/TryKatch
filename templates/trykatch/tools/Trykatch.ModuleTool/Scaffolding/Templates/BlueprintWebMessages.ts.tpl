import { useModuleI18n, type ModuleMessageValues } from '@__NPM_SCOPE__/module-sdk'
import type { DataTableLabels } from '@__NPM_SCOPE__/ui'

const defaults = {
  en: {

    actionsFor: 'Actions for {name}', application: 'Application', archive: 'Archive', cancel: 'Cancel', columns: 'Columns', comfortable: 'Comfortable', compact: 'Compact',
    createRecord: 'Create __ENTITY_LOWER__', description: 'Description', details: 'Details', edit: 'Edit', editRecord: 'Edit __ENTITY_LOWER__', editorDescription: 'Enter the record details.',
    emptyManage: 'Create the first record for this workspace.', emptyReadOnly: 'No records are available in this workspace.', emptyTitle: 'No __ENTITY_LOWER__ records',
    hideDetails: 'Hide details for {row}', loadFailed: '__MODULE__ could not be loaded', loading: 'Loading __MODULE__…', name: 'Name', newRecord: 'New __ENTITY_LOWER__',
    next: 'Next', noDescription: 'No description', noMatchingResults: 'No matching results.', page: 'Page {page} of {count}', pageDescription: 'Create and manage organization-owned __ENTITY_LOWER__ records.',
    previous: 'Previous', recordDetails: '__ENTITY__ details.', required: 'Required', result: 'result', results: 'results', rowDensity: 'Row density', save: 'Save', saving: 'Saving…',
    search: 'Search __MODULE__…', searchTable: 'Search table', showDetails: 'Show details for {row}', showing: 'Showing {start}–{end} of {total}', spacious: 'Spacious',
    status: 'Status', tableSettings: 'Table settings', closeTableSettings: 'Close table settings', tryAgain: 'Try again', view: 'View', notSet: 'Not set', yes: 'Yes', no: 'No',
  },
  fr: {

    actionsFor: 'Actions pour {name}', application: 'Application', archive: 'Archiver', cancel: 'Annuler', columns: 'Colonnes', comfortable: 'Confortable', compact: 'Compacte',
    createRecord: 'Créer __ENTITY_LOWER__', description: 'Description', details: 'Détails', edit: 'Modifier', editRecord: 'Modifier __ENTITY_LOWER__', editorDescription: 'Saisissez les détails de l’enregistrement.',
    emptyManage: 'Créez le premier enregistrement de cet espace de travail.', emptyReadOnly: 'Aucun enregistrement n’est disponible dans cet espace de travail.', emptyTitle: 'Aucun enregistrement __ENTITY_LOWER__',
    hideDetails: 'Masquer les détails de {row}', loadFailed: 'Impossible de charger __MODULE__', loading: 'Chargement de __MODULE__…', name: 'Nom', newRecord: 'Nouveau __ENTITY_LOWER__',
    next: 'Suivant', noDescription: 'Aucune description', noMatchingResults: 'Aucun résultat correspondant.', page: 'Page {page} sur {count}', pageDescription: 'Créez et gérez les enregistrements __ENTITY_LOWER__ de l’organisation.',
    previous: 'Précédent', recordDetails: 'Détails de __ENTITY__.', required: 'Obligatoire', result: 'résultat', results: 'résultats', rowDensity: 'Densité des lignes', save: 'Enregistrer', saving: 'Enregistrement…',
    search: 'Rechercher dans __MODULE__…', searchTable: 'Rechercher dans le tableau', showDetails: 'Afficher les détails de {row}', showing: 'Affichage de {start} à {end} sur {total}', spacious: 'Spacieuse',
    status: 'Statut', tableSettings: 'Paramètres du tableau', closeTableSettings: 'Fermer les paramètres du tableau', tryAgain: 'Réessayer', view: 'Afficher', notSet: 'Non défini', yes: 'Oui', no: 'Non',
  },
} as const

const messages = {
  en: { ...defaults.en, __BLUEPRINT_MESSAGES_EN__ },
  fr: { ...defaults.fr, __BLUEPRINT_MESSAGES_FR__ },
} as const

type MessageKey = keyof typeof messages.en

function interpolate(message: string, values?: ModuleMessageValues) {
  if (!values) return message
  return message.replace(/\{(\w+)\}/g, (match, key: string) => String(values[key] ?? match))
}

export function use__MODULE__Messages() {
  const { locale } = useModuleI18n()
  const selected = locale.toLowerCase().startsWith('fr') ? messages.fr : messages.en
  const t = (key: MessageKey, values?: ModuleMessageValues) => interpolate(selected[key], values)
  const tableLabels: DataTableLabels = {
    searchTable: t('searchTable'), result: t('result'), results: t('results'), columns: t('columns'), tableSettings: t('tableSettings'),
    closeTableSettings: t('closeTableSettings'), rowDensity: t('rowDensity'), compact: t('compact'), comfortable: t('comfortable'), spacious: t('spacious'),
    required: t('required'), details: t('details'), noMatchingResults: t('noMatchingResults'), showDetails: (row) => t('showDetails', { row }),
    hideDetails: (row) => t('hideDetails', { row }), showing: (start, end, total) => t('showing', { start, end, total }), previous: t('previous'),
    page: (page, count) => t('page', { page, count }), next: t('next'),
  }
  return { t, tableLabels, locale }
}
