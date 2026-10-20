import { useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Dialog } from '../../../components/ui/Dialog'
import { TextField } from '../../../components/ui/TextField'
import { toFormErrors } from '../../../lib/formErrors'
import { usersApi } from '../api/usersApi'
import type { UserDetails, UserRole } from '../types'
import { RoleSelect } from './RoleSelect'
import { TemporaryPasswordField } from './TemporaryPasswordField'
import styles from './UserDialogs.module.css'

interface NewUserDialogProps {
  onClose: () => void
  onCreated: (user: UserDetails) => void
}

export function NewUserDialog({ onClose, onCreated }: NewUserDialogProps) {
  const [userName, setUserName] = useState('')
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<UserRole>('User')
  const [password, setPassword] = useState('')
  const [mustChange, setMustChange] = useState(true)
  const [emailTwoFactor, setEmailTwoFactor] = useState(false)
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setFormError(null)
    const clientErrors: Record<string, string> = {}
    if (!userName.trim()) {
      clientErrors.userName = 'Enter a username.'
    }
    if (!email.trim()) {
      clientErrors.email = 'Enter an email address.'
    }
    if (!password) {
      clientErrors.password = 'Enter or generate a password.'
    }
    setErrors(clientErrors)
    if (Object.keys(clientErrors).length > 0) {
      return
    }

    setSaving(true)
    try {
      onCreated(await usersApi.create({ userName: userName.trim(), email: email.trim(), role, password, mustChangePassword: mustChange, emailTwoFactor }))
    } catch (error) {
      const { fields, message } = toFormErrors(error, ['userName', 'email', 'role', 'password', 'emailTwoFactor'])
      setErrors(fields)
      setFormError(message)
      setSaving(false)
    }
  }

  return (
    <Dialog
      title="New user"
      description="Users sign in with their username or email. Give them the password below."
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button type="submit" form="new-user" loading={saving}>
            Create user
          </Button>
        </>
      }
    >
      <form id="new-user" className={styles.form} onSubmit={handleSubmit} noValidate>
        {formError ? <Alert>{formError}</Alert> : null}
        <div className={styles.grid}>
          <TextField
            label="Username"
            value={userName}
            onChange={(e) => setUserName(e.target.value)}
            error={errors.userName}
            autoComplete="off"
            maxLength={64}
            data-autofocus
          />
          <TextField
            label="Email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            error={errors.email}
            autoComplete="off"
            maxLength={256}
          />
        </div>
        <RoleSelect value={role} onChange={setRole} error={errors.role} />
        <TemporaryPasswordField value={password} onChange={setPassword} error={errors.password} />
        <Checkbox label="Require a new password at first sign-in" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} />
        <Checkbox
          label="Email 2FA challenge code (a 9-digit code is emailed at every sign-in)"
          checked={emailTwoFactor}
          onChange={(e) => setEmailTwoFactor(e.target.checked)}
        />
        {errors.emailTwoFactor ? <Alert>{errors.emailTwoFactor}</Alert> : null}
      </form>
    </Dialog>
  )
}
