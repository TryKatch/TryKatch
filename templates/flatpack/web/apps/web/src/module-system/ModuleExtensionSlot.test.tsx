import { FlatpackWebModuleCatalog, defineFlatpackWebModule } from '@flatpackapp/module-sdk'
import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { FlatpackModuleProvider, ModuleExtensionSlot } from './ModuleExtensionSlot'

const PublicContribution = () => <span>Public contribution</span>
const ProtectedContribution = () => <span>Protected contribution</span>

const catalog = new FlatpackWebModuleCatalog([
  defineFlatpackWebModule({
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
    render(<FlatpackModuleProvider catalog={catalog}>
      <ModuleExtensionSlot point="reference.page.after" />
    </FlatpackModuleProvider>)

    expect(screen.getByText('Public contribution')).toBeInTheDocument()
    expect(screen.queryByText('Protected contribution')).not.toBeInTheDocument()
  })

  it('renders a protected contribution when its permission is present', () => {
    render(<FlatpackModuleProvider catalog={catalog}>
      <ModuleExtensionSlot point="reference.page.after" permissions={['reference.manage']} />
    </FlatpackModuleProvider>)

    expect(screen.getByText('Protected contribution')).toBeInTheDocument()
  })
})
