import { TrykatchWebModuleCatalog, defineTrykatchWebModule } from '@trykatchapp/module-sdk'
import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { TrykatchModuleProvider, ModuleExtensionSlot } from './ModuleExtensionSlot'
import { workspaceModules } from '../modules'

const PublicContribution = () => <span>Public contribution</span>
const ProtectedContribution = () => <span>Protected contribution</span>

const catalog = new TrykatchWebModuleCatalog([
  defineTrykatchWebModule({
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
    render(<TrykatchModuleProvider catalog={catalog}>
      <ModuleExtensionSlot point="reference.page.after" />
    </TrykatchModuleProvider>)

    expect(screen.getByText('Public contribution')).toBeInTheDocument()
    expect(screen.queryByText('Protected contribution')).not.toBeInTheDocument()
  })

  it('renders a protected contribution when its permission is present', () => {
    render(<TrykatchModuleProvider catalog={catalog}>
      <ModuleExtensionSlot point="reference.page.after" permissions={['reference.manage']} />
    </TrykatchModuleProvider>)

    expect(screen.getByText('Protected contribution')).toBeInTheDocument()
  })

  it('renders the installed Documents contribution at the Projects extension point', () => {
    render(<TrykatchModuleProvider catalog={workspaceModules}>
      <ModuleExtensionSlot point="projects.list.after-table" permissions={['documents.read']} />
    </TrykatchModuleProvider>)

    expect(screen.getByText('Documents module is active.')).toBeInTheDocument()
  })

  it('hides the installed Documents contribution without its read permission', () => {
    render(<TrykatchModuleProvider catalog={workspaceModules}>
      <ModuleExtensionSlot point="projects.list.after-table" permissions={['projects.read']} />
    </TrykatchModuleProvider>)

    expect(screen.queryByText('Documents module is active.')).not.toBeInTheDocument()
  })

  it('registers Documents recovery in the central archive boundary', () => {
    const documents = workspaceModules.archiveResources.find((resource) => resource.kind === 'document')

    expect(documents?.readPermission).toBe('documents.read')
    expect(documents?.managePermission).toBe('documents.manage')
    expect(documents?.requestDeletion).toBeTypeOf('function')
  })
})
