import { AuthLayout } from '../../../components/layout/AuthLayout'
import { PRODUCT_NAME } from '../../../app/config'
import { LoginForm } from '../components/LoginForm'
import { useAuth } from '../context'

/** Successful sign-in flips the auth state; PublicOnlyRoute then redirects to the original page. */
export function LoginPage() {
  const { login } = useAuth()

  return (
    <AuthLayout title={`Sign in to ${PRODUCT_NAME}`}>
      <LoginForm onSubmit={login} />
    </AuthLayout>
  )
}
