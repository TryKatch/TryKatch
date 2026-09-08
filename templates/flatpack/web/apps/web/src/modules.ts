import { FlatpackWebModuleCatalog, type FlatpackWebOverrides } from '@flatpackapp/module-sdk'
import { projectsModule } from './features/projects/module'

/** Application-owned overrides may replace or disable stable module contracts without patching module source. */
const workspaceOverrides = {} satisfies FlatpackWebOverrides

/** Explicit build-time registry. Flatpack package tooling updates this list. */
export const workspaceModules = new FlatpackWebModuleCatalog([
  projectsModule,
], workspaceOverrides)
