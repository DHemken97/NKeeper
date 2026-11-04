import { useEffect, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { ApiError } from '../../../lib/api'
import { useDbApi } from '../engineContext'
import type { DatabaseSessionSummary, ToolReport } from '../databaseApi'
import { ReportSections } from './ReportSections'
import styles from './Database.module.css'

/** Compare the row counts of a database's replicated tables here with a database on another open ASE server. */
export function DriftCompare({ sessionId }: { sessionId: string }) {
  const db = useDbApi()
  const [servers, setServers] = useState<DatabaseSessionSummary[]>([])
  const [databases, setDatabases] = useState<string[]>([])
  const [other, setOther] = useState('')
  const [database, setDatabase] = useState('')
  const [otherDatabase, setOtherDatabase] = useState('')
  const [exact, setExact] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<ToolReport | null>(null)

  async function loadServers() {
    try {
      const list = await db.sessions()
      setServers(list)
      setOther((current) => current || list[0]?.id || '')
    } catch {
      setServers([])
    }
  }

  useEffect(() => {
    const timer = window.setTimeout(() => {
      void loadServers()
      db.databases(sessionId).then(setDatabases, () => {})
    }, 0)
    return () => window.clearTimeout(timer)
    // Once, when the panel opens; "Refresh list" reloads the servers.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [db, sessionId])

  async function compare() {
    setBusy(true)
    setError(null)
    try {
      setResult(await db.compare(sessionId, { otherSessionId: other, database, otherDatabase: otherDatabase || undefined, exact }))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Couldn’t compare.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className={styles.reportPanel} aria-label="Compare with another server">
      <h3>Compare with another server</h3>
      <p className={styles.hint}>Counts the rows of the replicated tables in a database here and in its replicate. Open the other server in another tab to list it.</p>
      <div className={styles.reportForm}>
        <label className={styles.toolbarField}>
          <span>Database here</span>
          <select value={database} onChange={(event) => setDatabase(event.target.value)} aria-label="Database here">
            <option value="">Choose…</option>
            {databases.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
        </label>
        <label className={styles.toolbarField}>
          <span>Other server</span>
          <select value={other} onChange={(event) => setOther(event.target.value)} aria-label="Other server">
            {servers.length === 0 ? <option value="">No other ASE tab open</option> : null}
            {servers.map((s) => (
              <option key={s.id} value={s.id}>
                {s.serverName} · {s.display}
              </option>
            ))}
          </select>
        </label>
        <Button size="sm" variant="ghost" onClick={() => void loadServers()}>
          Refresh list
        </Button>
        <label className={styles.toolbarField}>
          <span>Database there</span>
          <input value={otherDatabase} onChange={(event) => setOtherDatabase(event.target.value)} placeholder={database || 'same name'} aria-label="Database there" />
        </label>
        <label className={styles.toolbarField}>
          <input type="checkbox" checked={exact} onChange={(event) => setExact(event.target.checked)} />
          <span>Exact counts (count(*): slower on big tables)</span>
        </label>
        <Button size="sm" onClick={() => void compare()} loading={busy} disabled={!database || !other}>
          Compare
        </Button>
      </div>
      {error ? <Alert variant="error">{error}</Alert> : null}
      {result?.warnings.map((warning) => (
        <Alert key={warning} variant="error">
          {warning}
        </Alert>
      ))}
      {result && result.warnings.length === 0 ? <Alert variant="info">Every table has the same count on both servers.</Alert> : null}
      {result ? <ReportSections sections={result.sections} /> : null}
    </section>
  )
}
