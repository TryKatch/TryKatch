import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { ProductLogo } from './ProductLogo'
import { LanguageSwitcher } from '../i18n/LanguageSwitcher'
import { LifecycleBadge, LifecycleFilter, RecordDetailsDialog, type LifecycleScope } from './RecordLifecycle'
import { SignOutDialog } from './SignOutDialog'
import { AccountSecurityPanel } from './AccountSecurityPanel'
import { Activity, ArchiveRestore, FileText, FolderKanban, LayoutDashboard, Search, ShieldCheck, Users } from 'lucide-react'

export default { title: 'Application UI/Shared surfaces' } satisfies Meta
type Story = StoryObj
export const BrandingAndPreferences: Story = { render: () => <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 24 }}><ProductLogo /><LanguageSwitcher /></div> }
export const Icons: Story = { render: () => <div style={{ display: 'flex', gap: 24, flexWrap: 'wrap' }}>{[Activity, ArchiveRestore, FileText, FolderKanban, LayoutDashboard, Search, ShieldCheck, Users].map((Icon, index) => <figure key={index}><Icon aria-hidden /><figcaption>{['Audit', 'Archive', 'Documents', 'Projects', 'Overview', 'Search', 'Security', 'Users'][index]}</figcaption></figure>)}</div> }
function LifecycleExample() {
  const [value, setValue] = useState<LifecycleScope>('active')
  return <><LifecycleFilter value={value} onChange={setValue} />{(['Active', 'Archived', 'Deleted'] as const).map((status) => <LifecycleBadge key={status} lifecycle={{ status }} />)}</>
}
export const Lifecycle: Story = { render: () => <LifecycleExample /> }
export const RecordDetails: Story = { render: () => <RecordDetailsDialog open title="Atlas" description="Project details" details={[{ label: 'Name', value: 'Atlas' }, { label: 'Description', value: 'Delivery programme' }]} onOpenChange={() => undefined} status={<LifecycleBadge lifecycle={{ status: 'Active' }} />} /> }
export const SignOut: Story = { render: () => <SignOutDialog open identity="preview@example.test" isPending={false} onOpenChange={() => undefined} onConfirm={() => undefined} /> }
export const SigningOut: Story = { render: () => <SignOutDialog open identity="preview@example.test" isPending onOpenChange={() => undefined} onConfirm={() => undefined} /> }
export const SignOutFailed: Story = { render: () => <SignOutDialog open identity="preview@example.test" isPending={false} error="Unable to end your session. Try again." onOpenChange={() => undefined} onConfirm={() => undefined} /> }
export const SecurityDisabled: Story = { render: () => <AccountSecurityPanel twoFactorEnabled={false} disabled /> }
export const SecurityEnabled: Story = { render: () => <AccountSecurityPanel twoFactorEnabled /> }
export const SecurityNotEnrolled: Story = { render: () => <AccountSecurityPanel twoFactorEnabled={false} /> }
