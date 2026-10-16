import type { ReactNode } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router'
import { FullPageStatus } from '../../../components/layout/FullPageStatus'
import { ChangePasswordPage } from '../../account/pages/ChangePasswordPage'
import { useAuth } from '../context'
import { isAdministrator } from '../types'
import { safeRedirect, type RedirectState } from '../redirect'

function SessionGate({ children }: { children: ReactNode }) {
  const { status, refresh } = useAuth()

  if (status === 'checking') {
    return <FullPageStatus kind="loading" message="Checking your session…" />
  }
  if (status === 'unavailable') {
    return (
      <FullPageStatus
        kind="error"
        message="We couldn't reach the NKeeper server. Check your connection and try again."
        onRetry={() => void refresh()}
      />
    )
  }
  return children
}

/**
 * Renders child routes only for authenticated users; others are sent to /login. A user whose
 * password was set by an administrator must choose a new one first.
 */
export function ProtectedRoute() {
  const { status, user } = useAuth()
  const location = useLocation()

  return (
    <SessionGate>
      {status === 'authenticated' ? (
        user?.mustChangePassword ? (
          <ChangePasswordPage />
        ) : (
          <Outlet />
        )
      ) : (
        <Navigate to="/login" replace state={{ from: location.pathname + location.search } satisfies RedirectState} />
      )}
    </SessionGate>
  )
}

/** For pages like /login that make no sense once signed in. */
export function PublicOnlyRoute() {
  const { status } = useAuth()
  const location = useLocation()
  const from = (location.state as RedirectState | null)?.from

  return <SessionGate>{status === 'authenticated' ? <Navigate to={safeRedirect(from)} replace /> : <Outlet />}</SessionGate>
}


/** Administrator-only pages; anyone else is sent to the workspace (the API enforces this too). */
export function AdminRoute() {
  const { user } = useAuth()
  return isAdministrator(user) ? <Outlet /> : <Navigate to="/" replace />
}
