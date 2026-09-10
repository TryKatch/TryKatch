export function ProductLogo({ size = 18 }: { size?: number }) {
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden="true">
    <path d="M3 3h18v4H3V3Zm4 5.5h10v4H7v-4Zm3 5.5h4v7h-4v-7Z" fill="currentColor" />
  </svg>
}
