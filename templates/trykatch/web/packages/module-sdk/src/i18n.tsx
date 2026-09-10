import { createContext, useContext, type ReactNode } from 'react'

export type ModuleMessageValues = Readonly<Record<string, string | number>>

export interface ModuleI18n {
  readonly locale: string
  t(message: string, values?: ModuleMessageValues): string
  formatDate(value: string | Date, options?: Intl.DateTimeFormatOptions): string
}

function interpolate(message: string, values?: ModuleMessageValues) {
  if (!values) return message
  return message.replace(/\{(\w+)\}/g, (match, key: string) => String(values[key] ?? match))
}

const defaultI18n: ModuleI18n = {
  locale: 'en',
  t: interpolate,
  formatDate: (value, options) => new Intl.DateTimeFormat('en', options).format(typeof value === 'string' ? new Date(value) : value),
}

const ModuleI18nContext = createContext<ModuleI18n>(defaultI18n)

export function ModuleI18nProvider({ children, value }: { children: ReactNode; value: ModuleI18n }) {
  return <ModuleI18nContext.Provider value={value}>{children}</ModuleI18nContext.Provider>
}

export function useModuleI18n() {
  return useContext(ModuleI18nContext)
}
