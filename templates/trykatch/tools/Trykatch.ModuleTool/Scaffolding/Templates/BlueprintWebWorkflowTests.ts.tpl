import { describe, expect, it, vi } from 'vitest'
import { runWorkflowAction, workflowActions, workflowError, workflowText, isStaleConflict } from './workflow'

vi.mock('@__NPM_SCOPE__/api-client', () => ({ __WEB_ACTION_MOCKS__ }))

describe('generated workflow contract', () => {
  it('has labels and bounded inputs for every action in both languages', () => {
    for (const action of workflowActions) {
      expect(workflowText(action.label, 'en')).not.toBe('')
      expect(workflowText(action.label, 'fr')).not.toBe('')
      for (const input of action.inputs) expect(input.maximumLength).toBeGreaterThan(0)
    }
  })

  it('rejects an unknown action instead of constructing an arbitrary URL', () => {
    expect(() => runWorkflowAction('unrecognized', 'id', 'version', {})).toThrow('Unknown workflow action')
  })

  it('distinguishes version conflicts from unexpected failures', () => {
    expect(isStaleConflict({ status: 409 })).toBe(true)
    expect(isStaleConflict({ status: 500 })).toBe(false)
    expect(workflowError({ status: 409 }, 'en')).toContain('preserved')
    expect(workflowError({ status: 409 }, 'fr')).toContain('conservées')
  })
})
