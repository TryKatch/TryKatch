import type { Meta, StoryObj } from '@storybook/react-vite'
import { http, HttpResponse } from 'msw'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { applicationName } from '../branding'
import { LoginPage } from './LoginPage'
import { ForgotPasswordPage } from './ForgotPasswordPage'
import { ResetPasswordPage } from './ResetPasswordPage'

export default { title: 'Application UI/Authentication', parameters: { layout: 'fullscreen' } } satisfies Meta
type Story = StoryObj
export const SignIn: Story = {
  render: () => <LoginPage navigate={() => undefined} />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByText(applicationName, { selector: 'strong' }).closest('a')).toHaveAttribute('href', '/')
    for (const input of Array.from(canvasElement.querySelectorAll<HTMLInputElement>('.floating-control-input'))) {
      const label = canvasElement.querySelector<HTMLLabelElement>(`label[for="${input.id}"]`)!
      const marker = label.querySelector('span')!
      // Exercise the real auth-card cascade, not only the isolated UI component.
      await expect(getComputedStyle(label).display).toBe('block')
      await expect(label.getBoundingClientRect().height).toBeLessThan(20)
      await expect(marker.getBoundingClientRect().top).toBeGreaterThanOrEqual(label.getBoundingClientRect().top)
      await expect(marker.getBoundingClientRect().bottom).toBeLessThanOrEqual(label.getBoundingClientRect().bottom + 1)
      await expect(input.getBoundingClientRect().height).toBe(38)
      await expect(getComputedStyle(input).borderTopColor).toBe('rgba(0, 0, 0, 0)')
      await userEvent.click(label)
      await expect(input).toHaveFocus()
      await waitFor(() => expect(label.getBoundingClientRect().top).toBeLessThan(input.getBoundingClientRect().top))
      await expect(getComputedStyle(input).boxShadow).toBe('none')
      await userEvent.type(input, input.type === 'email' ? 'alex@example.test' : 'Example-only-password')
      await userEvent.tab()
      await expect(label.getBoundingClientRect().top).toBeLessThan(input.getBoundingClientRect().top)
      await userEvent.clear(input)
      await userEvent.tab()
      await waitFor(() => expect(label.getBoundingClientRect().top).toBeGreaterThan(input.getBoundingClientRect().top + 8))
    }
    await expect(canvas.getByRole('button', { name: /Sign in|Se connecter/ })).toBeVisible()
  },
}
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
export const FrenchSignIn: Story = { ...SignIn, globals: { locale: 'fr' } }
export const DarkSignIn: Story = { ...SignIn, globals: { theme: 'dark' } }
