import { useId, type InputHTMLAttributes, type ReactNode, type Ref } from 'react'
import styles from './TextField.module.css'

export interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id'> {
  label: string
  error?: string
  /** Optional element rendered inside the input on the right (e.g. a show/hide toggle). */
  trailing?: ReactNode
  ref?: Ref<HTMLInputElement>
}

/** Labelled input whose validation error is announced and linked via aria-describedby. */
export function TextField({ label, error, trailing, className, ref, ...inputProps }: TextFieldProps) {
  const id = useId()
  const errorId = `${id}-error`

  return (
    <div className={`${styles.field} ${className ?? ''}`}>
      <label htmlFor={id} className={styles.label}>
        {label}
      </label>
      <div className={styles.control}>
        <input
          ref={ref}
          id={id}
          className={`${styles.input} ${trailing ? styles.withTrailing : ''}`}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          {...inputProps}
        />
        {trailing ? <div className={styles.trailing}>{trailing}</div> : null}
      </div>
      {error ? (
        <p id={errorId} className={styles.error}>
          {error}
        </p>
      ) : null}
    </div>
  )
}
