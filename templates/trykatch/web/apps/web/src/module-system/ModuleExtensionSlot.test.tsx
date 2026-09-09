import { TrykatchWebModuleCatalog, defineTrykatchWebModule } from '@trykatchapp/module-sdk'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { TrykatchModuleProvider, ModuleExtensionSlot } from './ModuleExtensionSlot'

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
})
