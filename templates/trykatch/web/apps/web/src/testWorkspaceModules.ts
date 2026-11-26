import { documentsModule } from '@trykatch-modules/documents'
import { projectsModule } from '@trykatch-modules/projects'
import { WebModuleCatalog } from '@trykatch/module-sdk'

/**
 * The feature and lifecycle tests below exercise the retained Projects and
 * Documents modules regardless of which generated profile the host catalog
 * represents.
 */
export const testWorkspaceModules = new WebModuleCatalog([projectsModule, documentsModule])
