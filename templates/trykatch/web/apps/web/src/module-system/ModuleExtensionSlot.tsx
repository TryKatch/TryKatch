import type { TrykatchWebModuleCatalog } from '@trykatchapp/module-sdk'
import { createContext, Suspense, useContext, type ReactNode } from 'react'

const ModuleCatalogContext = createContext<TrykatchWebModuleCatalog | null>(null)

export function TrykatchModuleProvider({ catalog, children }: { catalog: TrykatchWebModuleCatalog; children: ReactNode }) {
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
  if (!catalog) throw new Error('ModuleExtensionSlot must be rendered inside TrykatchModuleProvider.')

  return catalog.extensionsFor(point)
    .filter((extension) => !extension.requiredPermission || permissions.includes(extension.requiredPermission))
    .map(({ id, component: Extension }) => (
      <Suspense key={id} fallback={null}>
        <Extension context={context} />
      </Suspense>
    ))
}
