import { PRODUCT_NAME } from '../../../app/config'
import { useAuth } from '../../auth/context'
import styles from './DashboardPage.module.css'

/** Placeholder landing page. Connection management will replace the empty state. */
export function DashboardPage() {
  const { user } = useAuth()

  return (
    <div className={styles.page}>
      <div>
        <h1 className={styles.title}>Welcome to {PRODUCT_NAME}</h1>
        <p className={styles.subtitle}>
          Signed in as <strong>{user?.userName}</strong>
          {user?.email ? <> ({user.email})</> : null}
        </p>
      </div>

      <section className={styles.empty} aria-labelledby="coming-soon-title">
        <h2 id="coming-soon-title" className={styles.emptyTitle}>
          Connection management is on the way
        </h2>
        <p className={styles.emptyText}>
          Shared workspaces, connection folders, and RDP, SSH and VNC sessions will appear here in a future release.
        </p>
      </section>
    </div>
  )
}
