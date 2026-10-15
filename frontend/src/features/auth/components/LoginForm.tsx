import { useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { PasswordField } from '../../../components/ui/PasswordField'
import { TextField } from '../../../components/ui/TextField'
import { ApiError } from '../../../lib/api'
import { loginErrorMessage } from '../api/loginErrorMessage'
import type { LoginCredentials } from '../types'
import styles from './LoginForm.module.css'

interface LoginFormProps {
  /** Performs the sign-in. Should reject with an ApiError on failure. */
  onSubmit: (credentials: LoginCredentials) => Promise<void>
}

interface FieldErrors {
  userNameOrEmail?: string
  password?: string
}

function validate(values: LoginCredentials): FieldErrors {
  const errors: FieldErrors = {}
  if (!values.userNameOrEmail.trim()) {
    errors.userNameOrEmail = 'Enter your username or email.'
  }
  if (!values.password) {
    errors.password = 'Enter your password.'
  }
  return errors
}

export function LoginForm({ onSubmit }: LoginFormProps) {
  const [values, setValues] = useState<LoginCredentials>({ userNameOrEmail: '', password: '', rememberMe: false })
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const userNameRef = useRef<HTMLInputElement>(null)
  const passwordRef = useRef<HTMLInputElement>(null)

  function update<K extends keyof LoginCredentials>(key: K, value: LoginCredentials[K]) {
    setValues((current) => ({ ...current, [key]: value }))
    if (key in fieldErrors) {
      setFieldErrors((current) => ({ ...current, [key]: undefined }))
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (submitting) {
      return
    }

    const errors = validate(values)
    setFieldErrors(errors)
    setFormError(null)
    if (errors.userNameOrEmail) {
      userNameRef.current?.focus()
      return
    }
    if (errors.password) {
      passwordRef.current?.focus()
      return
    }

    setSubmitting(true)
    try {
      await onSubmit({ ...values, userNameOrEmail: values.userNameOrEmail.trim() })
      // On success the parent navigates away; this component unmounts.
    } catch (error) {
      setFormError(loginErrorMessage(error))
      if (error instanceof ApiError && error.status === 401) {
        setValues((current) => ({ ...current, password: '' }))
        passwordRef.current?.focus()
      }
      setSubmitting(false)
    }
  }

  return (
    <form className={styles.form} onSubmit={handleSubmit} noValidate aria-describedby={formError ? 'login-error' : undefined}>
      {formError ? (
        <div id="login-error">
          <Alert>{formError}</Alert>
        </div>
      ) : null}

      <TextField
        ref={userNameRef}
        label="Username or email"
        name="username"
        type="text"
        autoComplete="username"
        autoCapitalize="none"
        autoCorrect="off"
        spellCheck={false}
        inputMode="email"
        required
        value={values.userNameOrEmail}
        onChange={(e) => update('userNameOrEmail', e.target.value)}
        error={fieldErrors.userNameOrEmail}
      />

      <PasswordField
        ref={passwordRef}
        label="Password"
        name="password"
        autoComplete="current-password"
        required
        value={values.password}
        onChange={(e) => update('password', e.target.value)}
        error={fieldErrors.password}
      />

      <div className={styles.row}>
        <Checkbox
          label="Remember me"
          name="rememberMe"
          checked={values.rememberMe}
          onChange={(e) => update('rememberMe', e.target.checked)}
        />
        <Link to="/forgot-password" className={styles.link}>
          Forgot password?
        </Link>
      </div>

      <Button type="submit" fullWidth loading={submitting}>
        {submitting ? 'Signing in…' : 'Sign in'}
      </Button>
    </form>
  )
}
