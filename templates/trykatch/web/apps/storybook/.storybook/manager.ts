import { addons } from 'storybook/manager-api'
import { create } from 'storybook/theming'

addons.setConfig({ theme: create({ base: 'dark', brandTitle: 'Trykatch UI catalogue', colorPrimary: '#315fba', colorSecondary: '#9bb8ed' }) })
