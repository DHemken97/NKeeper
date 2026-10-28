import { Alert } from '../../../components/ui/Alert'
import { Badge } from '../../../components/ui/Badge'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { useResource } from '../../inventory/hooks/useResource'
import styles from '../../transfer/pages/TransferPage.module.css'
import { settingsApi, type PluginInfo } from '../settingsApi'
import own from '../pages/SettingsPage.module.css'

const STATUS: Record<PluginInfo['status'], { label: string; variant: 'accent' | 'neutral' }> = {
  loaded: { label: 'Loaded', variant: 'accent' },
  disabled: { label: 'Turned off', variant: 'neutral' },
  failed: { label: 'Not loaded', variant: 'neutral' },
}

/**
 * The plugins the server found at start-up: what each is, who signed it, and why one wasn't
 * loaded. Plugins are installed by putting their signed package in the server's plugins directory.
 */
export function PluginsPanel() {
  const overview = useResource((signal) => settingsApi.getPlugins(signal))

  return (
    <section className={styles.panel} aria-labelledby="plugins-title">
      <div>
        <h2 id="plugins-title" className={styles.panelTitle}>
          Plugins
        </h2>
        <p className={styles.hint}>
          Features that come as signed plugin packages, loaded when NKeeper starts. A package is only loaded when it’s signed by a key this server trusts and
          unchanged since. To add, upgrade or remove one, change the server’s plugins directory and restart it.
        </p>
      </div>
      {overview.error && !overview.data ? <Alert>{toFormErrors(overview.error).message}</Alert> : null}
      {!overview.data && !overview.error ? <Spinner label="Loading plugins" /> : null}
      {overview.data ? (
        <>
          {overview.data.plugins.length === 0 ? <p className={styles.hint}>No plugins are installed.</p> : null}
          <ul className={own.pluginList}>
            {overview.data.plugins.map((plugin) => (
              <li key={plugin.id} className={own.plugin}>
                <div className={own.pluginHeading}>
                  <strong>{plugin.name}</strong>
                  <span className={own.pluginMeta}>{plugin.version}</span>
                  <Badge variant={STATUS[plugin.status].variant}>{STATUS[plugin.status].label}</Badge>
                </div>
                {plugin.description ? <p className={own.pluginMeta}>{plugin.description}</p> : null}
                <p className={own.pluginMeta}>
                  {plugin.signer ? `Signed with key ${plugin.signer}` : 'Built into this server'}
                  {plugin.source ? ` · ${plugin.source}` : ''}
                  {plugin.dependencies.length > 0 ? ` · Needs ${plugin.dependencies.join(', ')}` : ''}
                </p>
                {plugin.error ? <Alert>{plugin.error}</Alert> : null}
              </li>
            ))}
          </ul>
          {overview.data.rejected.map((rejected) => (
            <Alert key={rejected.source}>{`${rejected.source} was refused: ${rejected.error}`}</Alert>
          ))}
          <p className={own.pluginMeta}>
            Plugin SDK {overview.data.sdkVersion}. Trusted signing keys: {overview.data.trustedKeys.length > 0 ? overview.data.trustedKeys.join(', ') : 'none'}.
          </p>
        </>
      ) : null}
    </section>
  )
}
