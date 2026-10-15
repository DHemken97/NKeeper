import { PRODUCT_NAME } from '../../app/config'
import styles from './Logo.module.css'

interface LogoProps {
  /** Icon size in pixels. */
  size?: number
  showName?: boolean
}

/** Placeholder geometric mark: two connected nodes forming an "N". Replace freely. */
export function Logo({ size = 32, showName = true }: LogoProps) {
  return (
    <span className={styles.logo}>
      <svg width={size} height={size} viewBox="0 0 32 32" aria-hidden="true" focusable="false">
        <rect width="32" height="32" rx="8" fill="var(--color-accent)" />
        <path
          d="M10 22V10l12 12V10"
          fill="none"
          stroke="#fff"
          strokeWidth="2.5"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
        <circle cx="10" cy="10" r="2.5" fill="#fff" />
        <circle cx="22" cy="22" r="2.5" fill="#fff" />
      </svg>
      {showName ? <span className={styles.name}>{PRODUCT_NAME}</span> : <span className="visually-hidden">{PRODUCT_NAME}</span>}
    </span>
  )
}
