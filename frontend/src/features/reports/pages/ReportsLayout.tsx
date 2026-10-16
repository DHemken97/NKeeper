import { NavLink, Outlet } from 'react-router'
import styles from './Reports.module.css'

/** The reports NKeeper offers, in the section's list. */
const REPORTS = [
  { path: 'ip-addresses', label: 'IP address map', description: 'Hosts by address, one column per /24 subnet' },
  { path: 'vm-resources', label: 'VM hosts & resources', description: 'Each VM host’s CPU and memory, and what its VMs use' },
]

/** The Reports section: the list of reports, and the chosen one. */
export function ReportsLayout() {
  return (
    <div className={styles.page}>
      <nav className={styles.sidebar} aria-label="Reports">
        <h1 className={styles.title}>Reports</h1>
        <ul className={styles.reportList}>
          {REPORTS.map((report) => (
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
