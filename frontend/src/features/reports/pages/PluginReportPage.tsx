import { Link, useParams } from 'react-router'
import { Spinner } from '../../../components/ui/Spinner'
import { PluginBoundary } from '../../../sdk/PluginSessionView'
import { usePlugins, usePluginsLoading } from '../../../sdk/registry'
import { useAuth } from '../../auth/context'
import { isAdministrator } from '../../auth/types'
import styles from './Reports.module.css'

/** A report a plugin adds, at /reports/{path}. */
export function PluginReportPage() {
  const { report: path } = useParams()
  const { user } = useAuth()
  const plugins = usePlugins()
  const loading = usePluginsLoading()
  const plugin = plugins.find((p) => p.reports?.some((r) => r.path === path))
  const report = plugin?.reports?.find((r) => r.path === path)

  if (!plugin || !report || (report.adminOnly && !isAdministrator(user))) {
    return loading ? (
      <Spinner label="Loading" />
    ) : (
      <div className={styles.state}>
        <p>There’s no such report. The plugin it belongs to may not be installed, or it’s for administrators only.</p>
        <Link to="/reports">All reports</Link>
      </div>
    )
  }

  const Report = report.component
  return (
    <PluginBoundary plugin={plugin.id}>
      <Report />
    </PluginBoundary>
  )
}
