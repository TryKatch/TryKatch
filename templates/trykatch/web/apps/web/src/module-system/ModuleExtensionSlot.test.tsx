import { WebModuleCatalog, defineWebModule } from '@trykatch/module-sdk'
import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { ModuleProvider, ModuleExtensionSlot } from './ModuleExtensionSlot'
import { workspaceModules } from '../modules'

const PublicContribution = () => <span>Public contribution</span>
const ProtectedContribution = () => <span>Protected contribution</span>

const catalog = new WebModuleCatalog([
  defineWebModule({
    id: 'reference',
    name: 'Reference',
    version: '1.0.0',
    description: 'Extension host test module.',
    requires: [],
    optionalDependencies: [],
    routes: [],
    navigation: [],
    extensionPoints: [{ id: 'reference.page.after', description: 'After the page', kind: 'ui-slot' }],
    extensions: [
      { id: 'reference.public', point: 'reference.page.after', order: 10, component: PublicContribution },
      { id: 'reference.protected', point: 'reference.page.after', order: 20, requiredPermission: 'reference.manage', component: ProtectedContribution },
    ],
  }),
])

afterEach(cleanup)

describe('ModuleExtensionSlot', () => {
  it('renders only contributions permitted for the current actor', () => {
    render(<ModuleProvider catalog={catalog}>
      <ModuleExtensionSlot point="reference.page.after" />
    </ModuleProvider>)

    expect(screen.getByText('Public contribution')).toBeInTheDocument()
    expect(screen.queryByText('Protected contribution')).not.toBeInTheDocument()
  })

  it('renders a protected contribution when its permission is present', () => {
    render(<ModuleProvider catalog={catalog}>
      <ModuleExtensionSlot point="reference.page.after" permissions={['reference.manage']} />
    </ModuleProvider>)

    expect(screen.getByText('Protected contribution')).toBeInTheDocument()
  })

  it('renders the installed Documents contribution at the Projects extension point', () => {
    render(<ModuleProvider catalog={workspaceModules}>
      <ModuleExtensionSlot point="projects.list.after-table" permissions={['documents.read']} />
    </ModuleProvider>)

    const contribution = screen.getByRole('region', { name: 'Documents workspace' })
    expect(within(contribution).getByText('Manage organization documents without leaving this workspace.')).toBeInTheDocument()
    expect(within(contribution).getByRole('link', { name: 'Open documents' })).toHaveAttribute('href', '/documents')
  })

  it('hides the installed Documents contribution without its read permission', () => {
    render(<ModuleProvider catalog={workspaceModules}>
      <ModuleExtensionSlot point="projects.list.after-table" permissions={['projects.read']} />
    </ModuleProvider>)

    expect(screen.queryByRole('region', { name: 'Documents workspace' })).not.toBeInTheDocument()
  })

  it('registers Documents recovery in the central archive boundary', () => {
    const documents = workspaceModules.archiveResources.find((resource) => resource.kind === 'document')

    expect(documents?.readPermission).toBe('documents.read')
    expect(documents?.managePermission).toBe('documents.manage')
    expect(documents?.requestDeletion).toBeTypeOf('function')
  })
})
