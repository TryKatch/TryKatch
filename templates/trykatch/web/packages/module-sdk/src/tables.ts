import type { CSSProperties, ReactNode } from 'react'
import type { WebExtensionPoint } from './index'

const rowType = Symbol('table row contract')
const contributionType = Symbol('typed table contribution')

export interface TableColumn<Row> {
  id: string
  header: string
  cell(row: Row): ReactNode
  sortValue?(row: Row): string | number | Date | null | undefined
  searchValue?(row: Row): string
  hideable?: boolean
  defaultVisible?: boolean
  width?: CSSProperties['width']
  align?: 'left' | 'right'
}

export interface TableAction {
  id: string
  label: string
  icon: 'view' | 'edit' | 'archive' | 'restore' | 'revoke' | 'delete'
  onSelect(): void
  disabled?: boolean
  danger?: boolean
}

export interface TableParts<Row> {
  columns?: readonly TableColumn<Row>[]
  actions?(row: Row): readonly TableAction[]
}

// Invariant Row prevents a contribution for one DTO from targeting another.
export interface TableExtensionPoint<Row> extends WebExtensionPoint {
  kind: 'data-table'
  readonly [rowType]: (row: Row) => Row
}

export interface WebTableContribution {
  readonly id: string
  readonly point: WebExtensionPoint
  readonly order: number
  readonly requiredPermission?: string
  readonly [contributionType]: { point: WebExtensionPoint; parts: unknown }
}

export function defineTableExtensionPoint<Row>(id: string, description: string): TableExtensionPoint<Row> {
  return Object.freeze({ id, description, kind: 'data-table', [rowType]: (row: Row) => row })
}

export function defineTableContribution<Row>(
  point: TableExtensionPoint<Row>,
  contribution: TableParts<Row> & { id: string; order: number; requiredPermission?: string },
): WebTableContribution {
  const { id, order, requiredPermission, ...parts } = contribution
  return Object.freeze({ id, order, requiredPermission, point, [contributionType]: { point, parts } })
}

export function hasValidTableBinding(contribution: WebTableContribution): boolean {
  return contribution[contributionType]?.point === contribution.point
}

export function resolveTableContributions<Row>(
  point: TableExtensionPoint<Row>, contributions: readonly WebTableContribution[], permissions: readonly string[],
  base: TableParts<Row> = {},
) {
  // Catalog validation guarantees point object identity. The sole erased cast
  // lives at this seam; applications and individual columns never cast DTOs.
  const parts = contributions
    .filter(item => item.point === point && (!item.requiredPermission || permissions.includes(item.requiredPermission)))
    .map(item => item[contributionType].parts as TableParts<Row>)
  const columns = [...(base.columns ?? []), ...parts.flatMap(part => part.columns ?? [])]
  ensureUniqueIds(columns, 'table column')
  return {
    columns,
    actions(row: Row): TableAction[] {
      const actions = [...(base.actions?.(row) ?? []), ...parts.flatMap(part => part.actions?.(row) ?? [])]
      ensureUniqueIds(actions, 'table action')
      return actions
    },
  }
}

function ensureUniqueIds(items: readonly { id: string }[], subject: string) {
  const seen = new Set<string>()
  for (const item of items) {
    if (!item.id.trim() || seen.has(item.id)) throw new Error(`Duplicate or empty Trykatch ${subject} id '${item.id}'.`)
    seen.add(item.id)
  }
}
