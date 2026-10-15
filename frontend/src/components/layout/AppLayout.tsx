import { useState } from 'react'
import { NavLink, Outlet } from 'react-router'
import { useAuth } from '../../features/auth/context'
import { Alert } from '../ui/Alert'
import { Button } from '../ui/Button'
import { Logo } from '../ui/Logo'
import styles from './AppLayout.module.css'

const NAV_ITEMS = [
  { to: '/', label: 'Overview', end: true },
  { to: '/hosts', label: 'Hosts' },
  { to: '/credentials', label: 'Credentials' },
  { to: '/groups', label: 'Groups & tags' },
]

/** Shell for authenticated pages: top bar with navigation, the signed-in user and sign-out. */
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
        <Logo size={28} />
        <nav className={styles.nav} aria-label="Main">
          {NAV_ITEMS.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className={({ isActive }) => `${styles.navLink} ${isActive ? styles.navLinkActive : ''}`}
            >
              {item.label}
            </NavLink>
          ))}
        </nav>
        <div className={styles.account}>
          <span className={styles.user} title={user?.email ?? undefined}>
            {user?.userName}
          </span>
          <Button variant="secondary" onClick={handleSignOut} loading={signingOut}>
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
