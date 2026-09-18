import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import roleStories, { RequiredFieldValidation, CorrectAndSave } from './roles.stories'
import { RoleEditorDialog } from './RoleEditorDialog'

const meta = {
  ...roleStories,
  title: 'Forms/Validation',
  component: RoleEditorDialog,
  parameters: { docs: { description: { component: 'Production form validation, not a decorative mock. Required errors are linked to their fields with aria-describedby and aria-invalid. Correct input removes the error; failed saves retain input. UI validation improves feedback but never replaces backend validation or authorization.' } } },
} satisfies Meta<typeof RoleEditorDialog>
export default meta
type Story = StoryObj<typeof meta>
export const ReadyToEdit: Story = {}
export const RequiredName: Story = { ...RequiredFieldValidation }
export const CorrectInvalidInput: Story = { ...CorrectAndSave }
export const Saving: Story = { args: { role: { name: 'Project operator', description: 'Project operations', permissions: ['projects.read'] }, isSaving: true } }
export const ServerErrorPreservesInput: Story = { args: { role: { name: 'Project operator', description: 'Project operations', permissions: ['projects.read'] }, error: 'A role with that name already exists. Choose another name and try again.' } }
export const FrenchValidation: Story = {
  globals: { locale: 'fr' },
  play: async ({ canvasElement }) => {
    const dialog = within(within(canvasElement.ownerDocument.body).getByRole('dialog'))
    await userEvent.click(dialog.getByRole('button', { name: 'Enregistrer le rôle' }))
    await expect(dialog.getByRole('alert')).toHaveTextContent('Le nom du rôle doit contenir entre 1 et 80 caractères.')
  },
}
export const DarkValidation: Story = { ...RequiredFieldValidation, globals: { theme: 'dark' } }
export const ConstrainedHeight: Story = {
  decorators: [(Story) => <><style>{'.role-editor-dialog { height: 400px; max-height: 400px; }'}</style><Story /></>],
  play: async ({ canvasElement }) => {
    const dialog = within(canvasElement.ownerDocument.body).getByRole('dialog')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Projects' }))
    const header = dialog.querySelector('.permission-editor-header')!
    const toolbar = dialog.querySelector('.permission-toolbar')!
    const footer = dialog.querySelector('.role-editor-footer')!
    await expect(header.getBoundingClientRect().bottom).toBeLessThanOrEqual(toolbar.getBoundingClientRect().top + 1)
    await expect(footer.getBoundingClientRect().bottom).toBeLessThanOrEqual(dialog.getBoundingClientRect().bottom)
    const body = dialog.querySelector<HTMLElement>('.role-editor-body') ?? dialog.querySelector<HTMLElement>('.permission-groups')!
    await expect(body.getBoundingClientRect().bottom).toBeLessThanOrEqual(footer.getBoundingClientRect().top + 1)
    await expect(body.scrollHeight).toBeGreaterThan(body.clientHeight)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save role' }))
    await expect(within(dialog).getByRole('alert')).toHaveTextContent('Role names must contain 1-80 characters.')
    body.scrollTop = body.scrollHeight
    await expect(footer.getBoundingClientRect().bottom).toBeLessThanOrEqual(dialog.getBoundingClientRect().bottom)
    await expect(within(dialog).getByRole('checkbox', { name: /Manage roles/ })).toBeDisabled()
  },
}
export const NarrowSaving: Story = {
  ...Saving,
  decorators: [(Story) => <><style>{'.role-editor-dialog { width: 300px; height: 400px; max-height: 400px; }'}</style><Story /></>],
  play: async ({ canvasElement }) => {
    const dialog = within(canvasElement.ownerDocument.body).getByRole('dialog')
    const body = dialog.querySelector<HTMLElement>('.role-editor-body')!
    await expect(body.scrollWidth).toBeLessThanOrEqual(body.clientWidth + 1)
    await expect(within(dialog).getByRole('button', { name: 'Saving…' })).toBeDisabled()
    await expect(within(dialog).getByRole('checkbox', { name: /Read projects/ })).toBeChecked()
  },
}
