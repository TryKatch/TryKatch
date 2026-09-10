import { createContext, Suspense, useContext, type ReactNode } from 'react'
import type { WebModuleCatalog } from './index'

const ModuleCatalogContext = createContext<WebModuleCatalog | null>(null)

export function ModuleProvider({ catalog, children }: { catalog: WebModuleCatalog; children: ReactNode }) {
  return <ModuleCatalogContext.Provider value={catalog}>{children}</ModuleCatalogContext.Provider>
}

export function ModuleExtensionSlot({
  point,
  context = {},
  permissions = [],
}: {
  point: string
  context?: Readonly<Record<string, unknown>>
  permissions?: readonly string[]
}) {
  const catalog = useContext(ModuleCatalogContext)
  if (!catalog) throw new Error('ModuleExtensionSlot must be rendered inside ModuleProvider.')

  return catalog.extensionsFor(point)
    .filter((extension) => !extension.requiredPermission || permissions.includes(extension.requiredPermission))
    .map(({ id, component: Extension }) => (
      <Suspense key={id} fallback={null}>
        <Extension context={context} />
      </Suspense>
    ))
}
