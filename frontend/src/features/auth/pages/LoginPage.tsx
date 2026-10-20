import { useEffect, useState } from 'react'
import { useLocation, useSearchParams } from 'react-router'
import { AuthLayout } from '../../../components/layout/AuthLayout'
import { PRODUCT_NAME } from '../../../app/config'
import { authApi } from '../api/authApi'
import { LoginForm } from '../components/LoginForm'
import { useAuth } from '../context'
import { safeRedirect, type RedirectState } from '../redirect'

/** Why single sign-on sent the user back here (/login?sso=…). */
const SINGLE_SIGN_ON_ERRORS: Record<string, string> = {
  failed: 'Single sign-on didn’t complete. Please try again.',
  refused: 'Your account isn’t allowed to sign in to NKeeper. Ask an administrator for access.',
}

/** Successful sign-in flips the auth state; PublicOnlyRoute then redirects to the original page. */
export function LoginPage() {
  const { login } = useAuth()
  const location = useLocation()
  const [params] = useSearchParams()
  const [singleSignOnName, setSingleSignOnName] = useState<string | null>(null)
  const returnTo = safeRedirect((location.state as RedirectState | null)?.from)

  useEffect(() => {
    const controller = new AbortController()
    void authApi.getSignInOptions(controller.signal).then((options) => {
      if (!controller.signal.aborted) {
        setSingleSignOnName(options.singleSignOnName)
      }
    })
    return () => controller.abort()
  }, [])

  return (
    <AuthLayout title={`Sign in to ${PRODUCT_NAME}`}>
      <LoginForm
        onSubmit={login}
        singleSignOnName={singleSignOnName}
        onSingleSignOn={(rememberMe) => window.location.assign(authApi.singleSignOnUrl(returnTo, rememberMe))}
        initialError={SINGLE_SIGN_ON_ERRORS[params.get('sso') ?? ''] ?? null}
      />
    </AuthLayout>
  )
}
