import { useCallback, useEffect, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { ApiError } from '../../../lib/api'
import { useDbApi, useDbEngine } from '../engineContext'
import type { SuspendedConnection, ToolKind, ToolReport, ToolScript } from '../sqlServerApi'
import { ResultGrid } from './ResultGrid'
import styles from './SqlServer.module.css'

/** The tools that are reports: replication (ASE, Replication Server) and Oracle's. */
export type ReportTool = Extract<ToolKind, 'replication' | 'storage' | 'backups' | 'health'>

const HEADINGS: Record<ReportTool, { title: string; subtitle: (repServer: boolean) => string }> = {
  replication: { title: 'Replication Health', subtitle: (repServer) => (repServer ? 'Threads, Queues and Stable Devices' : 'Transaction Logs and Rep Agents') },
  storage: { title: 'Storage', subtitle: () => 'Tablespaces and Datafiles' },
  backups: { title: 'Backups (RMAN)', subtitle: () => 'Backup History and Recovery Area' },
  health: { title: 'Health', subtitle: () => 'Limits, Alerts and Invalid Objects' },
}

interface ToolReportViewProps {
  tool: ReportTool
  /** The tab's own session (the report runs on a short connection of its own). */
  sessionId: string
  active: boolean
  /** Opens SQL a report offers in a new query window. */
  onOpenQuery: (sql: string) => void
}

/**
 * A Management report: warnings, then each section's results, with scripts to act on them (SQL opens in a
 * query window; RMAN commands are copied). Replication Health on ASE (the primary): each database's log, whether replication holds its truncation
 * point, and the Rep Agents. On Replication Server: what's down, threads, queues, stable devices and warm
 * standby, with Resume for suspended connections (optionally skipping the transaction that failed).
 */
export function ToolReportView({ tool, sessionId, active, onOpenQuery }: ToolReportViewProps) {
  const db = useDbApi()
  const engine = useDbEngine()
  const [report, setReport] = useState<ToolReport | null>(null)
  const [shown, setShown] = useState<ToolScript | null>(null)
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
      setReport(await (tool === 'replication' ? db.replication(sessionId) : db.report(sessionId, tool)))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : `Couldn’t read ${HEADINGS[tool].title}.`)
    } finally {
      setLoading(false)
    }
  }, [db, sessionId, tool])

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
    <div className={styles.blocking} hidden={!active} role="tabpanel" aria-label={HEADINGS[tool].title}>
      <header className={styles.blockingHeader}>
        <div>
          <h2>{HEADINGS[tool].title}</h2>
          <p>{HEADINGS[tool].subtitle(repServer)}</p>
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
          {section.note ? <p className={styles.hint}>{section.note}</p> : null}
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
          {section.scripts && section.scripts.length > 0 ? (
            <div className={styles.reportScripts} aria-label={`Scripts for ${section.title}`}>
              {section.scripts.map((script) => (
                <Button
                  key={script.label}
                  size="sm"
                  variant="secondary"
                  title={script.text}
                  onClick={() => (script.kind === 'sql' ? onOpenQuery(script.text) : setShown(script))}
                >
                  {script.label}
                  {script.kind === 'sql' ? '' : '…'}
                </Button>
              ))}
            </div>
          ) : null}
        </section>
      ))}

      {shown ? (
        <Dialog
          title={shown.label}
          onClose={() => setShown(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setShown(null)}>
                Close
              </Button>
              <Button onClick={() => void navigator.clipboard?.writeText(shown.text).catch(() => {})}>Copy</Button>
            </>
          }
        >
          <p>
            Run this on the database server: RMAN commands in <code>rman target /</code>, scripts starting with @ in SQL*Plus as SYSDBA.
          </p>
          <pre className={styles.replicationMessage}>{shown.text}</pre>
        </Dialog>
      ) : null}

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
