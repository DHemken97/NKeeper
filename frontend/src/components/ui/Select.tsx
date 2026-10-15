import { useId, type ReactNode, type SelectHTMLAttributes } from 'react'
import styles from './TextField.module.css'

interface SelectProps extends Omit<SelectHTMLAttributes<HTMLSelectElement>, 'id'> {
  label: string
  /** Render the label for screen readers only (e.g. in compact toolbars). */
  hideLabel?: boolean
  error?: string
  children: ReactNode
}

/** Labelled native select, styled to match TextField. */
export function Select({ label, hideLabel = false, error, className, children, ...selectProps }: SelectProps) {
  const id = useId()
  const errorId = `${id}-error`

  return (
    <div className={`${styles.field} ${className ?? ''}`}>
      <label htmlFor={id} className={hideLabel ? 'visually-hidden' : styles.label}>
        {label}
      </label>
      <select
        id={id}
        className={`${styles.input} ${styles.select}`}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        {...selectProps}
      >
        {children}
      </select>
      {error ? (
        <p id={errorId} className={styles.error}>
          {error}
        </p>
      ) : null}
    </div>
  )
}
