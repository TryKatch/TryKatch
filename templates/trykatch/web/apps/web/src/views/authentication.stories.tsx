import type { Meta, StoryObj } from '@storybook/react-vite'
import { http, HttpResponse } from 'msw'
import { expect, userEvent, within } from 'storybook/test'
import { LoginPage } from './LoginPage'
import { ForgotPasswordPage } from './ForgotPasswordPage'
import { ResetPasswordPage } from './ResetPasswordPage'
import { LandingPage } from './LandingPage'

export default { title: 'Application UI/Authentication', parameters: { layout: 'fullscreen' } } satisfies Meta
type Story = StoryObj
export const SignIn: Story = { render: () => <LoginPage navigate={() => undefined} /> }
export const InvalidCredentials: Story = {
  render: () => <LoginPage navigate={() => undefined} />,
  parameters: { msw: { handlers: { api: [http.post('*/api/v1/auth/login', () => HttpResponse.json({ title: 'Invalid credentials', detail: 'Check your email and password.' }, { status: 401 }))] } } },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await userEvent.type(canvas.getByRole('textbox', { name: /Email address/ }), 'preview@example.test')
    await userEvent.type(canvas.getByLabelText(/Password/, { selector: 'input' }), 'Not-a-real-password')
    await userEvent.click(canvas.getByRole('button', { name: 'Sign in' }))
    await expect(await canvas.findByText('Check your email and password.')).toBeVisible()
  },
}
export const ForgotPassword: Story = { render: () => <ForgotPasswordPage /> }
export const ResetPassword: Story = { render: () => <ResetPasswordPage /> }
export const Landing: Story = { render: () => <LandingPage /> }
export const FrenchSignIn: Story = { ...SignIn, globals: { locale: 'fr' } }
export const DarkSignIn: Story = { ...SignIn, globals: { theme: 'dark' } }
