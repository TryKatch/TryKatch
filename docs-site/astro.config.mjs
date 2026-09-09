import { defineConfig } from 'astro/config'
import starlight from '@astrojs/starlight'

export default defineConfig({
  site: 'https://docs.trykatch.net',
  integrations: [
    starlight({
      title: 'Trykatch',
      description: 'Build secure, modular .NET products on an enterprise application foundation.',
      favicon: '/favicon.svg',
      logo: {
        src: './src/assets/logo.svg',
        replacesTitle: false,
      },
      customCss: ['./src/styles/custom.css'],
      editLink: {
        baseUrl: 'https://github.com/TryKatch/TryKatch/edit/develop/docs-site/',
      },
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/TryKatch/TryKatch' },
      ],
      sidebar: [
        { label: 'Start here', items: ['index', 'getting-started/install'] },
        {
          label: 'Architecture',
          items: ['architecture/overview', 'architecture/security-and-tenancy'],
        },
        { label: 'Modules', items: ['modules/authoring'] },
        { label: 'Operations', items: ['operations/observability'] },
        {
          label: 'Reference',
          items: ['reference/template-options', 'reference/release-readiness'],
        },
      ],
    }),
  ],
})
