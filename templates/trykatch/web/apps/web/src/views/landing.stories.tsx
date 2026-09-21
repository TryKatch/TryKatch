import type { Meta, StoryObj } from '@storybook/react-vite'
import { LandingPreferencesMenu } from '../components/LandingPreferencesMenu'
import { LandingPage } from './LandingPage'

export default { title: 'Marketing/Landing page', parameters: { layout: 'fullscreen' } } satisfies Meta
type Story = StoryObj

export const Landing: Story = { render: () => <LandingPage /> }
export const Preferences: Story = { render: () => <LandingPreferencesMenu /> }
