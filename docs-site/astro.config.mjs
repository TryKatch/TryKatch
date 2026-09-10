import { defineConfig } from 'astro/config'
import starlight from '@astrojs/starlight'

export default defineConfig({
  site: 'https://docs.trykatch.net',
  integrations: [
    starlight({
      title: 'Trykatch',
      description: 'Build secure, modular .NET products on an enterprise application foundation.',
      disable404Route: true,
      favicon: '/favicon.svg',
      logo: {
        dark: './src/assets/logo-on-dark.svg',
        light: './src/assets/logo-on-light.svg',
        replacesTitle: false,
      },
      customCss: ['./src/styles/custom.css'],
      locales: {
        root: { label: 'English', lang: 'en' },
        fr: { label: 'Français', lang: 'fr' },
      },
      components: {
        ThemeSelect: './src/components/ThemeSelect.astro',
        LanguageSelect: './src/components/LanguageSelect.astro',
      },
      editLink: {
        baseUrl: 'https://github.com/TryKatch/TryKatch/edit/develop/docs-site/',
      },
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/TryKatch/TryKatch' },
      ],
      sidebar: [
        {
          label: 'Start here',
          translations: { fr: 'Bien démarrer' },
          items: ['index', 'getting-started/install'],
        },
        {
          label: 'Architecture',
          translations: { fr: 'Architecture' },
          items: ['architecture/overview', 'architecture/security-and-tenancy'],
        },
        { label: 'Modules', translations: { fr: 'Modules' }, items: ['modules/authoring'] },
        {
          label: 'Operations',
          translations: { fr: 'Exploitation' },
          items: ['operations/observability'],
        },
        {
          label: 'Reference',
          translations: { fr: 'Référence' },
          items: ['reference/template-options', 'reference/release-readiness'],
        },
      ],
    }),
  ],
})
