import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { customFetch } from '@trykatchapp/api-client'
import { defineTrykatchWebModule } from '@trykatchapp/module-sdk'
import { Badge, Button, Dialog, EmptyState, PageHeader, PasswordField, Skeleton, Surface } from '@trykatchapp/ui'
import { KeyRound, Plus, ShieldCheck } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import './styles.css'

interface Connection {
  id: string
  name: string
  issuer: string
  clientId: string
  secretConfigured: boolean
  enabled: boolean
  lastTestedAt?: string
  lastTestResult?: string
}

function SingleSignOnPage() {
  const queryClient = useQueryClient()
  const [createOpen, setCreateOpen] = useState(false)
  const connections = useQuery({ queryKey: ['federation', 'connections'], queryFn: () => customFetch<Connection[]>('/api/v1/platform/federation/connections/', { method: 'GET' }) })
  const create = useMutation({
    mutationFn: (input: { name: string; issuer: string; clientId: string; clientSecret: string }) => customFetch<Connection>('/api/v1/platform/federation/connections/', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) }),
    onSuccess: () => { setCreateOpen(false); queryClient.invalidateQueries({ queryKey: ['federation'] }) },
  })
  const test = useMutation({ mutationFn: (id: string) => customFetch(`/api/v1/platform/federation/connections/${id}/test`, { method: 'POST' }), onSuccess: () => queryClient.invalidateQueries({ queryKey: ['federation'] }) })
  const toggle = useMutation({ mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) => customFetch(`/api/v1/platform/federation/connections/${id}/${enabled ? 'enable' : 'disable'}`, { method: 'POST' }), onSuccess: () => queryClient.invalidateQueries({ queryKey: ['federation'] }) })

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    create.mutate({ name: String(data.get('name')), issuer: String(data.get('issuer')), clientId: String(data.get('clientId')), clientSecret: String(data.get('clientSecret')) })
  }

  return <>
    <PageHeader eyebrow="Authentication" title="Single sign-on" description="Test and activate trusted OpenID Connect providers." actions={<Button variant="primary" onClick={() => setCreateOpen(true)}><Plus size={14} /> Add connection</Button>} />
    <Surface className="federation-surface">
      {connections.isPending ? <div className="skeleton-list"><Skeleton /><Skeleton /></div>
        : connections.isError ? <EmptyState title="Connections could not be loaded" description={connections.error.message} />
          : !connections.data.length ? <EmptyState title="No SSO connections" description="Add an OIDC provider, test its discovery document, then enable it." />
            : <div className="federation-list">{connections.data.map((connection) => <article key={connection.id}>
              <span className="federation-icon"><KeyRound size={17} /></span>
              <div><strong>{connection.name}</strong><small>{connection.issuer}</small><small>{connection.lastTestResult ?? 'Not tested'}</small></div>
              <Badge tone={connection.enabled ? 'success' : undefined}>{connection.enabled ? 'Enabled' : 'Disabled'}</Badge>
              <div className="federation-actions"><Button onClick={() => test.mutate(connection.id)} disabled={test.isPending}>Test</Button><Button variant={connection.enabled ? 'secondary' : 'primary'} onClick={() => toggle.mutate({ id: connection.id, enabled: !connection.enabled })} disabled={toggle.isPending || (!connection.lastTestedAt && !connection.enabled)}>{connection.enabled ? 'Disable' : 'Enable'}</Button></div>
            </article>)}</div>}
    </Surface>
    <Dialog open={createOpen} onOpenChange={setCreateOpen} title="Add an SSO connection" description="Secrets are encrypted and cannot be read back.">
      <form className="federation-form" onSubmit={submit}>
        <label>Connection name<input name="name" minLength={2} maxLength={120} required autoFocus /></label>
        <label>Issuer URL<input name="issuer" type="url" placeholder="https://identity.example.com/realms/company" required /></label>
        <label>Client ID<input name="clientId" minLength={2} maxLength={240} autoComplete="off" required /></label>
        <PasswordField label="Client secret" name="clientSecret" autoComplete="new-password" visibilityLabel="client secret" required />
        {create.error && <div className="form-error" role="alert">{create.error.message}</div>}
        <div className="dialog-actions"><Button type="button" variant="ghost" onClick={() => setCreateOpen(false)}>Cancel</Button><Button type="submit" variant="primary" disabled={create.isPending}>{create.isPending ? 'Adding…' : 'Add connection'}</Button></div>
      </form>
    </Dialog>
  </>
}

export const federationModule = defineTrykatchWebModule({
  id: 'federation',
  name: 'Single sign-on',
  version: '1.0.0',
  description: 'Generic OpenID Connect connection management and federation security policy.',
  requires: [],
  optionalDependencies: [],
  routes: [{ id: 'federation.connections', surface: 'platform', path: '/authentication/sso', component: SingleSignOnPage }],
  navigation: [{ id: 'federation.navigation', surface: 'platform', section: 'Administration', order: 41, to: '/dashboard/authentication/sso', label: 'Single sign-on', icon: ShieldCheck, requiredPermission: 'platform.authentication.read' }],
  extensionPoints: [],
  extensions: [],
})
