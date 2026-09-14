export function toDateTimeLocal(value: string | null | undefined): string {
  if (!value) return ''
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return ''
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 23)
}

export function toUtcDateTime(value: string, original?: string | null): string {
  if (original && value === toDateTimeLocal(original)) return original
  return new Date(value).toISOString()
}
