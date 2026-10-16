import { useState, type FormEvent, type ReactNode } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { PasswordField } from '../../../components/ui/PasswordField'
import { toFormErrors } from '../../../lib/formErrors'
import { accountApi } from '../api/accountApi'
import styles from './ChangePasswordForm.module.css'

interface ChangePasswordFormProps {
  id: string
  onChanged: () => void
  onSubmittingChange?: (submitting: boolean) => void
  /** Rendered after the fields, e.g. the submit button. */
  children?: ReactNode
}

const MIN_LENGTH = 12

/** Current password, new password and confirmation, with the server's policy errors shown inline. */
export function ChangePasswordForm({ id, onChanged, onSubmittingChange, children }: ChangePasswordFormProps) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState<string | null>(null)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setFormError(null)
    const clientErrors: Record<string, string> = {}
    if (!current) {
      clientErrors.currentPassword = 'Enter your current password.'
    }
    if (next.length < MIN_LENGTH) {
      clientErrors.newPassword = `Use at least ${MIN_LENGTH} characters, with upper- and lower-case letters and a digit.`
    }
    if (confirm !== next) {
      clientErrors.confirm = 'The passwords don’t match.'
    }
    setErrors(clientErrors)
    if (Object.keys(clientErrors).length > 0) {
      return
    }

    onSubmittingChange?.(true)
    try {
      await accountApi.changePassword(current, next)
      onChanged()
    } catch (error) {
      const { fields, message } = toFormErrors(error, ['currentPassword', 'newPassword'])
      setErrors(fields)
      setFormError(message)
    } finally {
      onSubmittingChange?.(false)
    }
  }

  return (
    <form id={id} className={styles.form} onSubmit={handleSubmit} noValidate>
      {formError ? <Alert>{formError}</Alert> : null}
      <PasswordField
        label="Current password"
        value={current}
        onChange={(e) => setCurrent(e.target.value)}
        error={errors.currentPassword}
        autoComplete="current-password"
        data-autofocus
        autoFocus
      />
      <PasswordField
        label="New password"
        value={next}
        onChange={(e) => setNext(e.target.value)}
        error={errors.newPassword}
        hint={`At least ${MIN_LENGTH} characters, with upper- and lower-case letters and a digit.`}
        autoComplete="new-password"
      />
      <PasswordField
        label="Confirm new password"
        value={confirm}
        onChange={(e) => setConfirm(e.target.value)}
        error={errors.confirm}
        autoComplete="new-password"
      />
      {children}
    </form>
  )
}
