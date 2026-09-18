import type { RoleDto } from '@trykatch/api-client'
import { Button } from '@trykatch/ui'
import { useI18n } from '../i18n/I18nProvider'

export function OrganizationRolePicker({ roles, loading = false, error = false, retrying = false, onRetry }: {
  roles: readonly RoleDto[]; loading?: boolean; error?: boolean; retrying?: boolean; onRetry?: () => void
}) {
  const { t } = useI18n()
  const assignable = roles.filter((role) => role.canAssign)
  const defaultRole = assignable.find((role) => role.isSystem && role.name === 'Member') ?? assignable[0]
  return <fieldset className="organization-role-picker">
    <legend>{t('Workspace role')}</legend>
    <p>{t('Choose what this person can do when they join.')}</p>
    {loading ? <p role="status">{t('Loading workspace roles…')}</p> : error ? <div role="alert">
      <p>{t('Roles could not be loaded.')}</p>
      <Button type="button" variant="secondary" disabled={retrying} onClick={onRetry}>{t('Retry loading roles')}</Button>
    </div> : <>
    <div>{assignable.map((role) => <label key={role.id}>
      <input type="radio" name="roleId" aria-label={t(role.name)} value={role.id} defaultChecked={role.id === defaultRole?.id} required />
      <span><strong>{t(role.name)}</strong><small>{t(role.description || 'Custom workspace access')}</small></span>
    </label>)}</div>
    {assignable.length === 0 && <p role="alert">{t('No roles are available to assign. Contact the workspace owner.')}</p>}
    </>}
  </fieldset>
}
