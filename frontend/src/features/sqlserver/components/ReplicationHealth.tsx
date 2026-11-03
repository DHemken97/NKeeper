import { useCallback, useEffect, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { ApiError } from '../../../lib/api'
import { useDbApi, useDbEngine } from '../engineContext'
import type { ReplicationReport, SuspendedConnection } from '../sqlServerApi'
import { ResultGrid } from './ResultGrid'
import styles from './SqlServer.module.css'

interface ReplicationHealthProps {
  /** The tab's own session (the report runs on a short connection of its own). */
  sessionId: string
  active: boolean
}

/**
 * Replication Health. On ASE (the primary): each database's log, whether replication holds its truncation
 * point, and the Rep Agents. On Replication Server: what's down, threads, queues, stable devices and warm
 * standby, with Resume for suspended connections (optionally skipping the transaction that failed).
 */
export function ReplicationHealth({ sessionId, active }: ReplicationHealthProps) {
  const db = useDbApi()
  const engine = useDbEngine()
  const [report, setReport] = useState<ReplicationReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [resuming, setResuming] = useState<{ connection: SuspendedConnection; skip: boolean } | null>(null)
  const [resumeBusy, setResumeBusy] = useState(false)
  const [resumeError, setResumeError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setReport(await db.replication(sessionId))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Couldn’t read replication health.')
    } finally {
      setLoading(false)
    }
  }, [db, sessionId])

  useEffect(() => {
    if (!active) {
      return
    }
    // Read when the tab is shown (deferred, so a quick switch away doesn't start a read).
    const timer = window.setTimeout(() => void refresh(), 0)
    return () => window.clearTimeout(timer)
  }, [active, refresh])

  async function resume(connection: SuspendedConnection, skip: boolean) {
    setResumeBusy(true)
    setResumeError(null)
    try {
      await db.resumeConnection(sessionId, connection.connection, skip)
      setResuming(null)
      setNotice(`${connection.connection} was resumed${skip ? ', skipping the transaction that failed' : ''}.`)
      await refresh()
    } catch (e) {
      setResumeError(e instanceof ApiError ? e.message : `Couldn’t resume ${connection.connection}.`)
    } finally {
      setResumeBusy(false)
    }
  }

  const repServer = engine === 'rs'

  return (
    <div className={styles.blocking} hidden={!active} role="tabpanel" aria-label="Replication Health">
      <header className={styles.blockingHeader}>
        <div>
          <h2>Replication Health</h2>
          <p>{repServer ? 'Threads, Queues and Stable Devices' : 'Transaction Logs and Rep Agents'}</p>
        </div>
        <div className={styles.blockingActions}>
          <span className={styles.toolbarNote}>{report ? new Date(report.atUtc).toLocaleTimeString() : null}</span>
          <Button size="sm" onClick={() => void refresh()} loading={loading}>
            Refresh
          </Button>
        </div>
      </header>

      {notice ? <Alert variant="info">{notice}</Alert> : null}
      {error ? <Alert variant="error">{error}</Alert> : null}
      {report && report.warnings.length === 0 ? <Alert variant="info">Nothing needs attention.</Alert> : null}
      {report?.warnings.map((warning) => (
        <Alert key={warning} variant="error">
          {warning}
        </Alert>
      ))}

      {report && report.suspended.length > 0 ? (
        <section className={styles.replicationSection}>
          <h3>Suspended connections</h3>
          <table className={styles.blockingTable}>
            <thead>
              <tr>
                <th scope="col">Connection</th>
                <th scope="col">Thread</th>
                <th scope="col">State</th>
                <th scope="col">Actions</th>
              </tr>
            </thead>
            <tbody>
              {report.suspended.map((connection) => (
                <tr key={connection.connection}>
                  <td>{connection.connection}</td>
                  <td>{connection.thread}</td>
                  <td>{connection.state}</td>
                  <td>
                    <Button size="sm" variant="secondary" onClick={() => (setResumeError(null), setResuming({ connection, skip: false }))}>
                      Resume…
                    </Button>{' '}
                    <Button size="sm" variant="secondary" onClick={() => (setResumeError(null), setResuming({ connection, skip: true }))}>
                      Skip transaction…
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className={styles.hint}>Check the Replication Server errorlog for why it stopped before resuming.</p>
        </section>
      ) : null}

      {report?.sections.map((section) => (
        <section key={section.title} className={styles.replicationSection}>
          <h3>
            {section.title} <code>{section.command}</code>
          </h3>
          {section.error ? <Alert variant="error">{section.error}</Alert> : null}
          {section.results.map((result, index) =>
            result.kind === 'results' ? (
              <div key={index} className={styles.replicationGrid}>
                {result.rows && result.rows.length > 0 ? <ResultGrid output={result} label={`${section.title} results`} /> : <p className={styles.hint}>No rows.</p>}
              </div>
            ) : (
              <pre key={index} className={styles.replicationMessage}>
                {result.text}
              </pre>
            ),
          )}
          {!section.error && section.results.length === 0 ? <p className={styles.hint}>Nothing returned.</p> : null}
        </section>
      ))}

      {resuming ? (
        <Dialog
          title={`${resuming.skip ? 'Skip the failed transaction and resume' : 'Resume'} ${resuming.connection.connection}?`}
          onClose={() => !resumeBusy && setResuming(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setResuming(null)} disabled={resumeBusy}>
                Cancel
              </Button>
              <Button variant={resuming.skip ? 'danger' : 'primary'} onClick={() => void resume(resuming.connection, resuming.skip)} loading={resumeBusy}>
                {resuming.skip ? 'Skip and resume' : 'Resume'}
              </Button>
            </>
          }
        >
          <p>
            <code>
              resume connection to {resuming.connection.connection}
              {resuming.skip ? ' skip transaction' : ''}
            </code>
          </p>
          <p>
            {resuming.skip
              ? 'The transaction that failed is not applied to the replicate: Replication Server writes it to the exceptions log (rs_exceptions), and the replicate may then differ from the primary.'
              : 'Replication Server retries the transaction that failed. If what made it fail is not fixed, the connection suspends again.'}
          </p>
          {resumeError ? <Alert variant="error">{resumeError}</Alert> : null}
        </Dialog>
      ) : null}
    </div>
  )
}
