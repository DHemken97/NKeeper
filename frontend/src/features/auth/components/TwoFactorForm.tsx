import { useRef, useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { TextField } from '../../../components/ui/TextField'
import { ApiError } from '../../../lib/api'
import { authApi } from '../api/authApi'
import type { TwoFactorChallenge } from '../types'
import styles from './LoginForm.module.css'

interface TwoFactorFormProps {
  challenge: TwoFactorChallenge
  /** Finishes the sign-in; rejects with an ApiError for a wrong or expired code. */
  onVerify: (code: string) => Promise<void>
  /** Back to the password form, with a reason when the sign-in ended. */
  onCancel: (reason: string | null) => void
}

const EXPIRED = 'challenge_expired'

/** The second step for accounts with "Email 2FA challenge code": the 9-digit code from the email. */
export function TwoFactorForm({ challenge, onVerify, onCancel }: TwoFactorFormProps) {
  const [code, setCode] = useState('')
  const [sentTo, setSentTo] = useState(challenge.codeSentTo)
  const [message, setMessage] = useState<{ kind: 'info' | 'error'; text: string } | null>(null)
  const [busy, setBusy] = useState<'verify' | 'resend' | null>(null)
  const codeRef = useRef<HTMLInputElement>(null)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (busy) {
      return
    }
    if (code.replace(/\D/g, '').length !== 9) {
      setMessage({ kind: 'error', text: 'Enter the 9-digit code from the email.' })
      codeRef.current?.focus()
      return
    }

    setBusy('verify')
    setMessage(null)
    try {
      await onVerify(code)
      // On success the parent navigates away; this component unmounts.
    } catch (error) {
      if (error instanceof ApiError && error.code === EXPIRED) {
        onCancel('That sign-in expired or had too many wrong codes. Sign in again to get a new code.')
        return
      }
      setMessage({ kind: 'error', text: error instanceof ApiError && error.status === 401 ? 'That code isn’t right. Check the email and try again.' : 'Something went wrong. Please try again.' })
      setCode('')
      codeRef.current?.focus()
      setBusy(null)
    }
  }

  async function resend() {
    setBusy('resend')
    setMessage(null)
    try {
      setSentTo((await authApi.resendTwoFactor()).codeSentTo)
      setMessage({ kind: 'info', text: 'A new code is on its way. The previous one no longer works.' })
    } catch (error) {
      if (error instanceof ApiError && error.code === EXPIRED) {
        onCancel('That sign-in expired. Sign in again to get a new code.')
        return
      }
      setMessage({
        kind: 'error',
        text:
          error instanceof ApiError && error.status === 429
            ? 'A code was just sent. Wait a moment before asking for another.'
            : 'The code couldn’t be sent. Try again, or ask an administrator.',
      })
    } finally {
      setBusy(null)
    }
  }

  return (
    <form className={styles.form} onSubmit={handleSubmit} noValidate>
      <p className={styles.lead}>We emailed a 9-digit sign-in code to {sentTo}. It expires in 10 minutes.</p>
      {message ? <Alert variant={message.kind}>{message.text}</Alert> : null}

      <TextField
        ref={codeRef}
        label="Sign-in code"
        name="code"
        autoComplete="one-time-code"
        inputMode="numeric"
        spellCheck={false}
        maxLength={13}
        placeholder="123 456 789"
        required
        value={code}
        onChange={(e) => setCode(e.target.value)}
        data-autofocus
      />

      <Button type="submit" fullWidth loading={busy === 'verify'} disabled={busy === 'resend'}>
        {busy === 'verify' ? 'Checking…' : 'Verify'}
      </Button>

      <div className={styles.row}>
        <button type="button" className={styles.linkButton} onClick={() => void resend()} disabled={busy !== null}>
          Send a new code
        </button>
        <button
          type="button"
          className={styles.linkButton}
          disabled={busy !== null}
          onClick={() => {
            void authApi.cancelTwoFactor().catch(() => undefined)
            onCancel(null)
          }}
        >
          Use a different account
        </button>
      </div>
    </form>
  )
}
