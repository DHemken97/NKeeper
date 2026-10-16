import { useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Dialog } from '../../../components/ui/Dialog'
import { toFormErrors } from '../../../lib/formErrors'
import { usersApi } from '../api/usersApi'
import { TemporaryPasswordField } from './TemporaryPasswordField'
import styles from './UserDialogs.module.css'

interface ResetPasswordDialogProps {
  userId: string
  userName: string
  onClose: () => void
  onDone: () => void
}

export function ResetPasswordDialog({ userId, userName, onClose, onDone }: ResetPasswordDialogProps) {
  const [password, setPassword] = useState('')
  const [mustChange, setMustChange] = useState(true)
  const [error, setError] = useState<string | undefined>()
  const [formError, setFormError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setFormError(null)
    if (!password) {
      setError('Enter or generate a password.')
      return
    }
    setSaving(true)
    try {
      await usersApi.resetPassword(userId, password, mustChange)
      onDone()
    } catch (err) {
      const { fields, message } = toFormErrors(err, ['password'])
      setError(fields.password)
      setFormError(message)
      setSaving(false)
    }
  }

  return (
    <Dialog
      title={`Reset password for ${userName}`}
      description="This unlocks the account and signs the user out everywhere, including open SSH and RDP sessions."
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button type="submit" form="reset-password" loading={saving}>
            Reset password
          </Button>
        </>
      }
    >
      <form id="reset-password" className={styles.form} onSubmit={handleSubmit} noValidate>
        {formError ? <Alert>{formError}</Alert> : null}
        <TemporaryPasswordField value={password} onChange={setPassword} error={error} />
        <Checkbox label="Require a new password at next sign-in" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} />
      </form>
    </Dialog>
  )
}
