import { FlatpackWebModuleCatalog } from '@flatpackapp/module-sdk'
import { projectsModule } from './features/projects/module'

/** Explicit build-time registry. Flatpack package tooling updates this list. */
export const workspaceModules = new FlatpackWebModuleCatalog([
  projectsModule,
])
