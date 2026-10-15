import type { ReactNode } from 'react'
import styles from './Alert.module.css'

interface AlertProps {
  variant?: 'error' | 'info'
  children: ReactNode
}

/** Errors use role="alert" so screen readers announce them immediately. */
export function Alert({ variant = 'error', children }: AlertProps) {
  return (
    <div className={`${styles.alert} ${styles[variant]}`} role={variant === 'error' ? 'alert' : 'status'}>
      {children}
    </div>
  )
}
