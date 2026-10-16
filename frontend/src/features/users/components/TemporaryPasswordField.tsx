import { useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { PasswordField } from '../../../components/ui/PasswordField'
import { generatePassword } from '../types'
import styles from './UserDialogs.module.css'

interface TemporaryPasswordFieldProps {
  value: string
  onChange: (value: string) => void
  error?: string
}

/** A password field with "Generate" and "Copy", for passwords an administrator hands out. */
export function TemporaryPasswordField({ value, onChange, error }: TemporaryPasswordFieldProps) {
  const [copied, setCopied] = useState(false)
  return (
    <div className={styles.passwordRow}>
      <PasswordField
        className={styles.grow}
        label="Password"
        value={value}
        onChange={(e) => {
          setCopied(false)
          onChange(e.target.value)
        }}
        error={error}
        hint="At least 12 characters, with upper- and lower-case letters and a digit."
        autoComplete="new-password"
      />
      <div className={styles.passwordActions}>
        <Button
          variant="secondary"
          size="sm"
          onClick={() => {
            setCopied(false)
            onChange(generatePassword())
          }}
        >
          Generate
        </Button>
        <Button
          variant="ghost"
          size="sm"
          disabled={!value || !navigator.clipboard}
          onClick={() => void navigator.clipboard?.writeText(value).then(() => setCopied(true), () => {})}
        >
          {copied ? 'Copied' : 'Copy'}
        </Button>
      </div>
    </div>
  )
}
