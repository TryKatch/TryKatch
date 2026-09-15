import { __WEB_ACTION_IMPORTS__, type __ENTITY__Dto } from '@__NPM_SCOPE__/api-client'

interface Text { en: string; fr: string }
interface WorkflowInput { name: string; required: boolean; minimumLength: number | null; maximumLength: number; label: Text }
interface WorkflowAction { id: string; label: Text; inputs: WorkflowInput[] }
export const workflowActions: readonly WorkflowAction[] = __WEB_ACTION_METADATA__
const labels: Record<string, Text> = __WEB_WORKFLOW_LABELS__

export function workflowText(value: string | Text, locale: string): string {
  const text = typeof value === 'string' ? labels[value] : value
  return text ? (locale.startsWith('fr') ? text.fr : text.en) : String(value)
}

export function runWorkflowAction(action: string, id: string, expectedVersion: string, values: Readonly<Record<string, string>>): Promise<__ENTITY__Dto> {
  switch (action) {
    __WEB_ACTION_DISPATCH__
    default: throw new Error('Unknown workflow action.')
  }
}

export function isStaleConflict(error: unknown): boolean {
  return typeof error === 'object' && error !== null && 'status' in error && error.status === 409
}

export function workflowError(error: unknown, locale: string): string {
  const french = locale.startsWith('fr')
  if (typeof error === 'object' && error !== null && 'status' in error && error.status === 403)
    return french ? 'Vous n’avez pas la permission d’effectuer cette action.' : 'You do not have permission to perform this action.'
  if (isStaleConflict(error)) return french
    ? 'Cet enregistrement a changé ou cette action n’est plus autorisée dans son état actuel. Chargez la version récente ; vos saisies sont conservées.'
    : 'This record changed or this action is no longer allowed in its current state. Load the latest version; your entered values are preserved.'
  if (typeof error === 'object' && error !== null && 'problem' in error && typeof error.problem === 'object' && error.problem !== null) {
    const problem = error.problem
    if ('code' in problem && typeof problem.code === 'string' && labels[problem.code]) return workflowText(problem.code, locale)
    if ('field' in problem && typeof problem.field === 'string') {
      const name = workflowText(problem.field, locale)
      return french ? `Vérifiez la valeur du champ « ${name} » et ses limites.` : `Check the value and allowed limits for ${name}.`
    }
    if ('errors' in problem && typeof problem.errors === 'object' && problem.errors !== null) {
      return Object.entries(problem.errors).map(([field, errors]) => `${workflowText(field, locale)}: ${Array.isArray(errors) ? errors.join(' ') : ''}`).join('\n')
    }
  }
  return error instanceof Error ? error.message : french ? 'La requête a échoué.' : 'The request failed.'
}
