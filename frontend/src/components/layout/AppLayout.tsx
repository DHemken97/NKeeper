import { useState } from 'react'
import { Outlet } from 'react-router'
import { useAuth } from '../../features/auth/context'
import { Alert } from '../ui/Alert'
import { Button } from '../ui/Button'
import { Logo } from '../ui/Logo'
import styles from './AppLayout.module.css'

/** Shell for authenticated pages: a compact top bar, and the page filling the rest of the window. */
export function AppLayout() {
  const { user, logout } = useAuth()
  const [signingOut, setSigningOut] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSignOut() {
    setSigningOut(true)
    setError(null)
    try {
      await logout()
    } catch {
      setError('Sign-out failed. Check your connection and try again.')
      setSigningOut(false)
    }
  }

  return (
    <div className={styles.shell}>
      <header className={styles.header}>
        <Logo size={24} />
        <div className={styles.account}>
          <span className={styles.user} title={user?.email ?? undefined}>
            {user?.userName}
          </span>
          <Button variant="secondary" size="sm" onClick={handleSignOut} loading={signingOut}>
            Sign out
          </Button>
        </div>
      </header>
      {error ? (
        <div className={styles.banner}>
          <Alert>{error}</Alert>
        </div>
      ) : null}
      <main className={styles.content}>
        <Outlet />
      </main>
    </div>
  )
}
