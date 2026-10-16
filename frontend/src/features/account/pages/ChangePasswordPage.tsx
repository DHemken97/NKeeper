import { useState } from 'react'
import { AuthLayout } from '../../../components/layout/AuthLayout'
import { Button } from '../../../components/ui/Button'
import { useAuth } from '../../auth/context'
import { ChangePasswordForm } from '../components/ChangePasswordForm'
import styles from './ChangePasswordPage.module.css'

/** Shown instead of the app until a user replaces a password an administrator assigned. */
export function ChangePasswordPage() {
  const { user, passwordChanged, logout } = useAuth()
  const [submitting, setSubmitting] = useState(false)

  return (
    <AuthLayout title="Choose a new password">
      <p className={styles.intro}>
        Welcome, <strong>{user?.userName}</strong>. Your administrator set a temporary password; replace it to continue.
      </p>
      <ChangePasswordForm id="first-password" onChanged={passwordChanged} onSubmittingChange={setSubmitting}>
        <Button type="submit" fullWidth loading={submitting}>
          Save password and continue
        </Button>
        <Button variant="ghost" fullWidth onClick={() => void logout()} disabled={submitting}>
          Sign out
        </Button>
      </ChangePasswordForm>
    </AuthLayout>
  )
}
