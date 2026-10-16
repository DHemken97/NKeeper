import { useState, type FormEvent } from 'react'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { PasswordField } from '../../../components/ui/PasswordField'
import { TextField } from '../../../components/ui/TextField'
import styles from './CredentialPromptDialog.module.css'

interface CredentialPromptDialogProps {
  /** e.g. "sql01 · SSH :22" */
  target: string
  /** VNC servers usually ask for a password only. */
  usernameOptional?: boolean
  /** Prefilled from a saved credential that has no password; the password field then has focus. */
  initialUsername?: string
  /** The submit button, e.g. "Scan"; "Connect" by default. */
  submitLabel?: string
  onConnect: (username: string, password: string) => void
  onCancel: () => void
}

/** Asks for a one-off username and password. Nothing is saved to the host. */
export function CredentialPromptDialog({
  target,
  usernameOptional = false,
  initialUsername = '',
  submitLabel = 'Connect',
  onConnect,
  onCancel,
}: CredentialPromptDialogProps) {
  const [username, setUsername] = useState(initialUsername)
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!username.trim() && !usernameOptional) {
      setError('Enter a username.')
      return
    }
    onConnect(username.trim(), password)
  }

  return (
    <Dialog
      title="Enter credentials"
      description={target}
      onClose={onCancel}
      footer={
        <>
          <Button variant="secondary" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="submit" form="credential-prompt">
            {submitLabel}
          </Button>
        </>
      }
    >
      <form id="credential-prompt" className={styles.form} onSubmit={handleSubmit} noValidate>
        <TextField
          label={usernameOptional ? 'Username (optional)' : 'Username'}
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          error={error ?? undefined}
          autoComplete="username"
          spellCheck={false}
          maxLength={256}
          data-autofocus={initialUsername ? undefined : true}
        />
        <PasswordField
          label="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          autoComplete="current-password"
          maxLength={1024}
          data-autofocus={initialUsername ? true : undefined}
        />
        <p className={styles.hint}>
          {initialUsername
            ? 'Used this time only and not saved. To save it, edit the host and set the credential’s password.'
            : 'Used this time only and not saved. To save it, add a credential to the host.'}
        </p>
      </form>
    </Dialog>
  )
}
