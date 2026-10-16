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
  onConnect: (username: string, password: string) => void
  onCancel: () => void
}

/** Asks for a one-off username and password. Nothing is saved to the host. */
export function CredentialPromptDialog({ target, usernameOptional = false, onConnect, onCancel }: CredentialPromptDialogProps) {
  const [username, setUsername] = useState('')
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
            Connect
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
          data-autofocus
        />
        <PasswordField
          label="Password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          autoComplete="current-password"
          maxLength={1024}
        />
        <p className={styles.hint}>Used for this session only and not saved. To save it, add a credential to the host.</p>
      </form>
    </Dialog>
  )
}
