import type { NavigationContribution } from '@trykatch/module-sdk'

export function navigationLabel(
  item: Pick<NavigationContribution, 'label' | 'labels'>,
  locale: string,
  translate: (label: string) => string,
): string {
  return item.labels ? (locale.startsWith('fr') ? item.labels.fr : item.labels.en) : translate(item.label)
}
