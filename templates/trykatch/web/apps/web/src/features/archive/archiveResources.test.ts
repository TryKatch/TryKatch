import { afterEach, describe, expect, it, vi } from 'vitest'
import { customFetch } from '@trykatch/api-client'
import { archiveResourceDefinitions, archiveTimestamp, canRequestArchiveItemDeletion, canRestoreArchiveItem, loadArchiveItems, requestArchiveItemDeletion, restoreArchiveItem, type ArchiveItem } from './archiveResources'

vi.mock('@trykatch/api-client', async (original) => ({
  ...await original<typeof import('@trykatch/api-client')>(),
  customFetch: vi.fn(),
}))

const deletedProject: ArchiveItem = {
  id: 'project-1',
  kind: 'project',
  typeLabel: 'Project',
  title: 'Atlas',
  description: 'Reference project',
  lifecycle: { status: 'Deleted', deletedAt: '2026-09-07T10:00:00Z', deletionReason: 'Created in error' },
}

describe('archive resource registry', () => {
  afterEach(() => vi.resetAllMocks())

  it('binds project archive reads and management to separate installed features', () => {
    expect(archiveResourceDefinitions.find((resource) => resource.kind === 'project')).toMatchObject({
      readFeature: 'projects.reads',
      manageFeature: 'projects.writes',
    })
  })

  it('authorizes restore from the resource manage permission', () => {
    expect(canRestoreArchiveItem(deletedProject, ['projects.read'], () => true)).toBe(false)
    expect(canRestoreArchiveItem(deletedProject, ['projects.read', 'projects.manage'], () => true)).toBe(true)
  })

  it('uses the recoverable lifecycle timestamp for ordering', () => {
    expect(archiveTimestamp(deletedProject)).toBe(new Date('2026-09-07T10:00:00Z').getTime())
  })

  it('offers deletion only after an eligible resource has been archived', () => {
    expect(canRequestArchiveItemDeletion(deletedProject, ['projects.manage'], () => true)).toBe(false)
    expect(canRequestArchiveItemDeletion({ ...deletedProject, lifecycle: { status: 'Archived' } }, ['projects.manage'], () => true)).toBe(true)
    expect(canRequestArchiveItemDeletion({ ...deletedProject, kind: 'invitation', lifecycle: { status: 'Archived' } }, ['members.manage'], () => true)).toBe(false)
  })

  it('requires a read feature independently from read permission and keeps a manage feature independent', () => {
    const archiveProject = { ...deletedProject, lifecycle: { status: 'Archived' as const } }
    const onlyWrites = (key: string) => key === 'projects.writes'
    const onlyReads = (key: string) => key === 'projects.reads'

    expect(canRestoreArchiveItem(deletedProject, ['projects.manage'], onlyWrites)).toBe(true)
    expect(canRestoreArchiveItem(deletedProject, ['projects.manage'], onlyReads)).toBe(false)
    expect(canRestoreArchiveItem(deletedProject, ['projects.read'], onlyWrites)).toBe(false)
    expect(canRequestArchiveItemDeletion(archiveProject, ['projects.manage'], onlyWrites)).toBe(true)
    expect(canRequestArchiveItemDeletion(archiveProject, ['projects.manage'], onlyReads)).toBe(false)
  })

  it('does not query feature-bound project records when reads are disabled or unknown', async () => {
    const enabled = (key: string) => key === 'projects.reads'
    const disabled = () => false

    await loadArchiveItems(['projects.read'], disabled)
    expect(customFetch).not.toHaveBeenCalled()

    vi.mocked(customFetch).mockResolvedValue({ items: [], page: 1, pageSize: 100, totalCount: 0 } as never)
    await loadArchiveItems(['projects.read'], enabled)
    expect(customFetch).toHaveBeenCalledTimes(1)

    vi.mocked(customFetch).mockClear()
    await loadArchiveItems(['projects.read'], (key) => key !== 'projects.reads')
    expect(customFetch).not.toHaveBeenCalled()
  })

  it('does not let an enabled read feature grant the read permission', async () => {
    await loadArchiveItems([], () => true)
    expect(customFetch).not.toHaveBeenCalled()
  })

  it('rechecks current writes availability before restore and deletion requests', async () => {
    const off = () => false
    await expect(restoreArchiveItem(deletedProject, off)).rejects.toThrow()
    await expect(requestArchiveItemDeletion({ ...deletedProject, lifecycle: { status: 'Archived' } }, 'Reason long enough', off)).rejects.toThrow()
    expect(customFetch).not.toHaveBeenCalled()
  })
})
