import { Link } from 'react-router'
import { AuthLayout } from '../../../components/layout/AuthLayout'
import { Alert } from '../../../components/ui/Alert'

/** Placeholder until self-service password recovery is implemented. */
export function ForgotPasswordPage() {
  return (
    <AuthLayout title="Reset your password" footer={<Link to="/login">Back to sign in</Link>}>
      <Alert variant="info">
        Self-service password recovery isn&apos;t available yet. Contact your NKeeper administrator to have your
        password reset.
      </Alert>
    </AuthLayout>
  )
}
