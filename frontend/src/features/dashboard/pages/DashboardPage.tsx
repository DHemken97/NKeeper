import { Link } from 'react-router'
import { PRODUCT_NAME } from '../../../app/config'
import { useAuth } from '../../auth/context'
import styles from './DashboardPage.module.css'

const SECTIONS = [
  { to: '/hosts', title: 'Hosts', text: 'Servers and devices with their addresses, services and credentials.' },
  { to: '/credentials', title: 'Credentials', text: 'Shared accounts, passwords and SSH keys, encrypted at rest.' },
  { to: '/groups', title: 'Groups & tags', text: 'Organize hosts by site, customer or environment.' },
]

/** Landing page after sign-in: entry points into the inventory. */
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

      <nav className={styles.cards} aria-label="Inventory">
        {SECTIONS.map((section) => (
          <Link key={section.to} to={section.to} className={styles.card}>
            <span className={styles.cardTitle}>{section.title}</span>
            <span className={styles.cardText}>{section.text}</span>
          </Link>
        ))}
      </nav>

      <p className={styles.note}>
        Connecting to hosts over RDP, SSH, VNC and more is coming in a future release. For now, build your inventory.
      </p>
    </div>
  )
}
