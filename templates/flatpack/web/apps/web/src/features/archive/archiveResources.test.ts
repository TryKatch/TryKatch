import { describe, expect, it } from 'vitest'
import { archiveTimestamp, canRequestArchiveItemDeletion, canRestoreArchiveItem, type ArchiveItem } from './archiveResources'

const deletedProject: ArchiveItem = {
  id: 'project-1',
  kind: 'project',
  typeLabel: 'Project',
  title: 'Atlas',
  description: 'Reference project',
  lifecycle: { status: 'Deleted', deletedAt: '2026-09-07T10:00:00Z', deletionReason: 'Created in error' },
}

describe('archive resource registry', () => {
  it('authorizes restore from the resource manage permission', () => {
    expect(canRestoreArchiveItem(deletedProject, ['projects.read'])).toBe(false)
    expect(canRestoreArchiveItem(deletedProject, ['projects.read', 'projects.manage'])).toBe(true)
  })

  it('uses the recoverable lifecycle timestamp for ordering', () => {
    expect(archiveTimestamp(deletedProject)).toBe(new Date('2026-09-07T10:00:00Z').getTime())
  })

  it('offers deletion only after an eligible resource has been archived', () => {
    expect(canRequestArchiveItemDeletion(deletedProject, ['projects.manage'])).toBe(false)
    expect(canRequestArchiveItemDeletion({ ...deletedProject, lifecycle: { status: 'Archived' } }, ['projects.manage'])).toBe(true)
    expect(canRequestArchiveItemDeletion({ ...deletedProject, kind: 'invitation', lifecycle: { status: 'Archived' } }, ['members.manage'])).toBe(false)
  })
})
