import type { RoleDto } from '@trykatch/api-client'
import { useI18n } from '../i18n/I18nProvider'

export function OrganizationRolePicker({ roles }: { roles: readonly RoleDto[] }) {
  const { t } = useI18n()
  const assignable = roles.filter((role) => role.canAssign)
  const defaultRole = assignable.find((role) => role.isSystem && role.name === 'Member') ?? assignable[0]
  return <fieldset className="organization-role-picker">
    <legend>{t('Workspace role')}</legend>
    <p>{t('Choose what this person can do when they join.')}</p>
    <div>{assignable.map((role) => <label key={role.id}>
      <input type="radio" name="roleId" aria-label={t(role.name)} value={role.id} defaultChecked={role.id === defaultRole?.id} required />
      <span><strong>{t(role.name)}</strong><small>{t(role.description || 'Custom workspace access')}</small></span>
    </label>)}</div>
    {assignable.length === 0 && <p role="alert">{t('No roles are available to assign. Contact the workspace owner.')}</p>}
  </fieldset>
}
