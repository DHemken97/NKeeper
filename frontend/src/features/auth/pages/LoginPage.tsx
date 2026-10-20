import { useEffect, useState } from 'react'
import { useLocation, useSearchParams } from 'react-router'
import { AuthLayout } from '../../../components/layout/AuthLayout'
import { PRODUCT_NAME } from '../../../app/config'
import { authApi } from '../api/authApi'
import { LoginForm } from '../components/LoginForm'
import { TwoFactorForm } from '../components/TwoFactorForm'
import { useAuth } from '../context'
import { safeRedirect, type RedirectState } from '../redirect'
import type { TwoFactorChallenge } from '../types'

/** Why single sign-on sent the user back here (/login?sso=…). */
const SINGLE_SIGN_ON_ERRORS: Record<string, string> = {
  failed: 'Single sign-on didn’t complete. Please try again.',
  refused: 'Your account isn’t allowed to sign in to NKeeper. Ask an administrator for access.',
  email: 'Your account needs a sign-in code sent by email, and it couldn’t be sent. Ask an administrator to check the email settings.',
}

/**
 * Successful sign-in flips the auth state; PublicOnlyRoute then redirects to the original page.
 * Accounts with "Email 2FA challenge code" get a second step for the emailed code, also when
 * single sign-on sends them back here with ?twoFactor=1.
 */
export function LoginPage() {
  const { login, verifyTwoFactor } = useAuth()
  const location = useLocation()
  const [params] = useSearchParams()
  const [singleSignOnName, setSingleSignOnName] = useState<string | null>(null)
  const [challenge, setChallenge] = useState<TwoFactorChallenge | null>(null)
  const [notice, setNotice] = useState<string | null>(SINGLE_SIGN_ON_ERRORS[params.get('sso') ?? ''] ?? null)
  const returnTo = safeRedirect((location.state as RedirectState | null)?.from)
  const pendingTwoFactor = params.get('twoFactor') === '1'

  useEffect(() => {
    const controller = new AbortController()
    void authApi.getSignInOptions(controller.signal).then((options) => {
      if (!controller.signal.aborted) {
        setSingleSignOnName(options.singleSignOnName)
      }
    })
    if (pendingTwoFactor) {
      void authApi.getTwoFactorChallenge(controller.signal).then((pending) => {
        if (!controller.signal.aborted) {
          setChallenge(pending)
        }
      })
    }
    return () => controller.abort()
  }, [pendingTwoFactor])

  return (
    <AuthLayout title={challenge ? 'Check your email' : `Sign in to ${PRODUCT_NAME}`}>
      {challenge ? (
        <TwoFactorForm
          challenge={challenge}
          onVerify={verifyTwoFactor}
          onCancel={(reason) => {
            setNotice(reason)
            setChallenge(null)
          }}
        />
      ) : (
        <LoginForm
          // Remount so a new notice shows.
          key={notice ?? ''}
          onSubmit={async (credentials) => setChallenge(await login(credentials))}
          singleSignOnName={singleSignOnName}
          onSingleSignOn={(rememberMe) => window.location.assign(authApi.singleSignOnUrl(returnTo, rememberMe))}
          initialError={notice}
        />
      )}
    </AuthLayout>
  )
}
