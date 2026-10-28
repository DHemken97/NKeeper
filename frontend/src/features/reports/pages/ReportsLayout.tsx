import { NavLink, Outlet } from 'react-router'
import { useAuth } from '../../auth/context'
import { isAdministrator } from '../../auth/types'
import { usePlugins } from '../../../sdk/registry'
import styles from './Reports.module.css'

/** The reports NKeeper offers, in the section's list. */
const REPORTS = [
  { path: 'inventory', label: 'Inventory', description: 'Host counts by tag, OS, service, username and more' },
  { path: 'ip-addresses', label: 'IP address map', description: 'Hosts by address, one column per /24 subnet' },
  { path: 'health', label: 'Health', description: 'What’s down, and certificates about to expire' },
  { path: 'vm-resources', label: 'VM hosts & resources', description: 'Each VM host’s CPU and memory, and what its VMs use' },
  { path: 'audit', label: 'Audit log', description: 'Sign-ins, sessions and changes, with recordings', adminOnly: true },
]

/** The Reports section: the list of reports, and the chosen one. */
export function ReportsLayout() {
  const { user } = useAuth()
  const admin = isAdministrator(user)
  const plugins = usePlugins()
  // Plugins' reports go before the audit log, which stays last.
  const reports = [...REPORTS.slice(0, -1), ...plugins.flatMap((p) => p.reports ?? []), ...REPORTS.slice(-1)]
  return (
    <div className={styles.page}>
      <nav className={styles.sidebar} aria-label="Reports">
        <h1 className={styles.title}>Reports</h1>
        <ul className={styles.reportList}>
          {reports.filter((report) => admin || !report.adminOnly).map((report) => (
            <li key={report.path}>
              <NavLink to={report.path} className={({ isActive }) => `${styles.reportLink} ${isActive ? styles.reportLinkActive : ''}`}>
                <span>{report.label}</span>
                <span className={styles.reportDescription}>{report.description}</span>
              </NavLink>
            </li>
          ))}
        </ul>
      </nav>
      <section className={styles.report}>
        <Outlet />
      </section>
    </div>
  )
}
