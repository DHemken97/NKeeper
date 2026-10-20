import { useState } from 'react'
import { NavLink, Outlet, useNavigate } from 'react-router'
import { ChangePasswordDialog } from '../../features/account/components/ChangePasswordDialog'
import { useAuth } from '../../features/auth/context'
import { isAdministrator } from '../../features/auth/types'
import { Alert } from '../ui/Alert'
import { ContextMenu } from '../ui/ContextMenu'
import { Logo } from '../ui/Logo'
import styles from './AppLayout.module.css'

/** Shell for authenticated pages: a compact top bar, and the page filling the rest of the window. */
export function AppLayout() {
  const { user, logout } = useAuth()
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null)
  const [changingPassword, setChangingPassword] = useState(false)
  const [notice, setNotice] = useState<{ kind: 'error' | 'info'; text: string } | null>(null)
  const admin = isAdministrator(user)
  const navigate = useNavigate()

  async function handleSignOut() {
    setNotice(null)
    try {
      await logout()
    } catch {
      setNotice({ kind: 'error', text: 'Sign-out failed. Check your connection and try again.' })
    }
  }

  return (
    <div className={styles.shell}>
      <header className={styles.header}>
        <Logo size={24} />
        <nav className={styles.nav} aria-label="Main">
          <NavLink to="/" end className={({ isActive }) => `${styles.navLink} ${isActive ? styles.navLinkActive : ''}`}>
            Hosts
          </NavLink>
          <NavLink to="/reports" className={({ isActive }) => `${styles.navLink} ${isActive ? styles.navLinkActive : ''}`}>
            Reports
          </NavLink>
          {admin ? (
            <NavLink to="/users" className={({ isActive }) => `${styles.navLink} ${isActive ? styles.navLinkActive : ''}`}>
              Users
            </NavLink>
          ) : null}
        </nav>
        <button
          type="button"
          className={styles.accountButton}
          aria-haspopup="menu"
          aria-expanded={menu !== null}
          title={user?.email ?? undefined}
          onClick={(event) => {
            const rect = event.currentTarget.getBoundingClientRect()
            setMenu({ x: rect.right - 200, y: rect.bottom + 4 })
          }}
        >
          <span className={styles.avatar} aria-hidden="true">
            {user?.userName.slice(0, 1).toUpperCase()}
          </span>
          <span className={styles.user}>{user?.userName}</span>
          <svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true">
            <path d="M2.5 4.5 6 8l3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
          </svg>
        </button>
      </header>
      {notice ? (
        <div className={styles.banner}>
          <Alert variant={notice.kind}>{notice.text}</Alert>
        </div>
      ) : null}
      <main className={styles.content}>
        <Outlet />
      </main>

      {menu ? (
        <ContextMenu
          label="Account"
          x={menu.x}
          y={menu.y}
          onClose={() => setMenu(null)}
          entries={[
            { kind: 'header', label: user?.email ?? user?.userName ?? '' },
            // A domain account's password is changed in Windows; single sign-on accounts have none.
            ...(user?.isDirectoryAccount || user?.isSingleSignOnAccount ? [] : [{ kind: 'item', label: 'Change password', onSelect: () => setChangingPassword(true) } as const]),
            ...(admin ? [{ kind: 'item', label: 'Move to a new server…', onSelect: () => navigate('/transfer') } as const] : []),
            { kind: 'separator' },
            { kind: 'item', label: 'Sign out', onSelect: () => void handleSignOut() },
          ]}
        />
      ) : null}
      {changingPassword ? (
        <ChangePasswordDialog
          onClose={() => setChangingPassword(false)}
          onChanged={() => {
            setChangingPassword(false)
            setNotice({ kind: 'info', text: 'Your password was changed. Other sessions were signed out.' })
          }}
        />
      ) : null}
    </div>
  )
}
