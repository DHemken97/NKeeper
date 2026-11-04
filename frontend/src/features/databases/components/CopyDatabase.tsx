import { useEffect, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { ApiError } from '../../../lib/api'
import { useDbApi } from '../engineContext'
import type { AseCopyPlan, AseCopyStep, DatabaseSessionSummary, ToolSection } from '../databaseApi'
import { ReportSections } from './ReportSections'
import styles from './Database.module.css'

interface CopyDatabaseProps {
  /** The tab's own session: the source server. */
  sessionId: string
  active: boolean
}

const STEP_LABELS: Record<AseCopyStep, string> = {
  create: 'Create the target database (for load)',
  dump: 'Dump the database here',
  load: 'Load it on the target',
  online: 'Bring it online',
  settrunc: 'Clear its replication truncation point',
}

type StepState = { state: 'waiting' } | { state: 'running' } | { state: 'done'; result: ToolSection } | { state: 'failed'; result: ToolSection | null; message: string }

/**
 * Copy Database: dump a database on this server and load it on another open ASE server, as ASE does it. Check
 * first (page size, version, character set, room, a target not in use); then run the steps one by one, or all.
 */
export function CopyDatabase({ sessionId, active }: CopyDatabaseProps) {
  const db = useDbApi()
  const [databases, setDatabases] = useState<string[]>([])
  const [servers, setServers] = useState<DatabaseSessionSummary[]>([])
  const [database, setDatabase] = useState('')
  const [target, setTarget] = useState('')
  const [targetDatabase, setTargetDatabase] = useState('')
  const [checking, setChecking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [plan, setPlan] = useState<AseCopyPlan | null>(null)
  const [createSql, setCreateSql] = useState('')
  const [remoteBackupServer, setRemoteBackupServer] = useState('')
  const [dumpPath, setDumpPath] = useState('')
  const [loadPath, setLoadPath] = useState('')
  const [compress, setCompress] = useState(true)
  const [clearTruncation, setClearTruncation] = useState(true)
  const [steps, setSteps] = useState<Partial<Record<AseCopyStep, StepState>>>({})
  const [confirming, setConfirming] = useState<AseCopyStep[] | null>(null)

  async function loadServers() {
    try {
      const list = await db.sessions()
      setServers(list)
      setTarget((current) => current || list[0]?.id || '')
    } catch {
      setServers([])
    }
  }

  useEffect(() => {
    if (!active) {
      return
    }
    const timer = window.setTimeout(() => {
      void loadServers()
      db.databases(sessionId).then(setDatabases, () => {})
    }, 0)
    return () => window.clearTimeout(timer)
    // When the tab is shown; "Refresh list" reloads the servers.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, db, sessionId])

  const name = targetDatabase.trim() || database
  const stepList: AseCopyStep[] = plan
    ? [...(plan.createSql ? (['create'] as const) : []), 'dump', 'load', 'online', ...(plan.replicated && clearTruncation ? (['settrunc'] as const) : [])]
    : []
  const running = Object.values(steps).some((s) => s?.state === 'running')

  async function check() {
    setChecking(true)
    setError(null)
    setPlan(null)
    setSteps({})
    try {
      const next = await db.copyCheck(sessionId, { targetSessionId: target, database, targetDatabase: name })
      setPlan(next)
      setCreateSql(next.createSql ?? '')
      setDumpPath((current) => current || `/tmp/${database}.dmp`)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Couldn’t check the copy.')
    } finally {
      setChecking(false)
    }
  }

  async function run(step: AseCopyStep): Promise<boolean> {
    setSteps((current) => ({ ...current, [step]: { state: 'running' } }))
    try {
      const result = await db.copyRun(sessionId, {
        targetSessionId: target,
        database,
        targetDatabase: name,
        step,
        dumpPath,
        loadPath: loadPath || dumpPath,
        remoteBackupServer: remoteBackupServer || undefined,
        compress,
        createSql: step === 'create' ? createSql : undefined,
      })
      const ok = !result.error
      setSteps((current) => ({ ...current, [step]: ok ? { state: 'done', result } : { state: 'failed', result, message: result.error ?? 'It failed.' } }))
      return ok
    } catch (e) {
      setSteps((current) => ({ ...current, [step]: { state: 'failed', result: null, message: e instanceof ApiError ? e.message : 'It failed.' } }))
      return false
    }
  }

  async function runAll(list: AseCopyStep[]) {
    setConfirming(null)
    for (const step of list) {
      if (!(await run(step))) {
        return
      }
    }
  }

  const blocked = !plan || plan.blockers.length > 0
  const remaining = stepList.filter((s) => steps[s]?.state !== 'done')

  return (
    <div className={styles.toolPage} hidden={!active} role="tabpanel" aria-label="Copy Database">
      <header className={styles.toolHeader}>
        <div>
          <h2>Copy Database</h2>
          <p>Dump Here, Load on Another Server</p>
        </div>
      </header>

      <section className={styles.reportPanel}>
        <div className={styles.reportForm}>
          <label className={styles.toolbarField}>
            <span>Database</span>
            <select value={database} onChange={(event) => (setDatabase(event.target.value), setPlan(null))} aria-label="Database to copy">
              <option value="">Choose…</option>
              {databases.map((d) => (
                <option key={d} value={d}>
                  {d}
                </option>
              ))}
            </select>
          </label>
          <label className={styles.toolbarField}>
            <span>To server</span>
            <select value={target} onChange={(event) => (setTarget(event.target.value), setPlan(null))} aria-label="Target server">
              {servers.length === 0 ? <option value="">No ASE tab open</option> : null}
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
            <span>As</span>
            <input value={targetDatabase} onChange={(event) => (setTargetDatabase(event.target.value), setPlan(null))} placeholder={database || 'same name'} aria-label="Target database" />
          </label>
          <Button size="sm" onClick={() => void check()} loading={checking} disabled={!database || !target || running}>
            Check
          </Button>
        </div>
        <p className={styles.hint}>Open the target server in another tab (Connect ▸ Tools ▸ SAP ASE) to choose it here.</p>
        {error ? <Alert variant="error">{error}</Alert> : null}
      </section>

      {plan ? (
        <section className={styles.reportPanel} aria-label="Copy plan">
          <h3>
            {database} on {plan.source.server} ({plan.source.version}) → {name} on {plan.target.server} ({plan.target.version})
          </h3>
          <p className={styles.hint}>
            Data {plan.dataMb.toFixed(0)} MB, log {plan.logMb.toFixed(0)} MB.
          </p>
          {plan.blockers.map((b) => (
            <Alert key={b} variant="error">
              {b}
            </Alert>
          ))}
          {plan.warnings.map((w) => (
            <Alert key={w} variant="info">
              {w}
            </Alert>
          ))}

          {plan.createSql ? (
            <label className={styles.reportField}>
              <span>{name} doesn’t exist there: it’s created for the load (adjust devices and sizes if you like)</span>
              <textarea value={createSql} onChange={(event) => setCreateSql(event.target.value)} rows={2} spellCheck={false} aria-label="Create statement" />
            </label>
          ) : null}

          <div className={styles.reportForm}>
            <label className={styles.toolbarField}>
              <span>Dump through</span>
              <select value={remoteBackupServer} onChange={(event) => setRemoteBackupServer(event.target.value)} aria-label="Dump through">
                <option value="">This server’s Backup Server (a path here)</option>
                {plan.backupServers.map((s) => (
                  <option key={s} value={s}>
                    {s} (a path on its machine)
                  </option>
                ))}
              </select>
            </label>
            <label className={styles.toolbarField}>
              <span>Dump file</span>
              <input value={dumpPath} onChange={(event) => setDumpPath(event.target.value)} aria-label="Dump file" spellCheck={false} />
            </label>
            <label className={styles.toolbarField}>
              <span>Target reads it from</span>
              <input value={loadPath} onChange={(event) => setLoadPath(event.target.value)} placeholder={dumpPath} aria-label="Load file" spellCheck={false} />
            </label>
            <label className={styles.toolbarField}>
              <input type="checkbox" checked={compress} onChange={(event) => setCompress(event.target.checked)} />
              <span>Compress</span>
            </label>
            {plan.replicated ? (
              <label className={styles.toolbarField}>
                <input type="checkbox" checked={clearTruncation} onChange={(event) => setClearTruncation(event.target.checked)} />
                <span>Clear the copy’s replication truncation point</span>
              </label>
            ) : null}
          </div>
          <p className={styles.hint}>
            The target’s Backup Server must be able to read the file: dump through it (a server the source knows in sysservers), or use storage both
            machines share.
          </p>

          <ol className={styles.copySteps}>
            {stepList.map((step) => {
              const state = steps[step] ?? { state: 'waiting' }
              return (
                <li key={step}>
                  <div className={styles.copyStep}>
                    <span>{STEP_LABELS[step]}</span>
                    <span className={styles.toolbarNote}>
                      {state.state === 'running' ? 'Running…' : state.state === 'done' ? 'Done' : state.state === 'failed' ? 'Failed' : ''}
                    </span>
                    <Button
                      size="sm"
                      variant="secondary"
                      disabled={blocked || running}
                      loading={state.state === 'running'}
                      onClick={() => (step === 'load' ? setConfirming([step]) : void run(step))}
                    >
                      Run
                    </Button>
                  </div>
                  {state.state === 'failed' ? <Alert variant="error">{state.message}</Alert> : null}
                  {state.state === 'done' || (state.state === 'failed' && state.result) ? (
                    <ReportSections sections={[state.state === 'done' ? state.result : state.result!]} />
                  ) : null}
                </li>
              )
            })}
          </ol>
          <Button onClick={() => setConfirming(remaining)} disabled={blocked || running || remaining.length === 0}>
            Run {remaining.length === stepList.length ? 'all steps' : 'the rest'}
          </Button>
        </section>
      ) : null}

      {confirming ? (
        <Dialog
          title={`Copy ${database} to ${name} on ${plan?.target.server}?`}
          onClose={() => setConfirming(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setConfirming(null)}>
                Cancel
              </Button>
              <Button variant="danger" onClick={() => void (confirming.length === 1 ? (setConfirming(null), run(confirming[0])) : runAll(confirming))}>
                {confirming.length === 1 ? 'Load' : 'Run'}
              </Button>
            </>
          }
        >
          <p>
            Loading replaces everything in {name} on {plan?.target.server}. A big database takes a while: the steps keep running on the servers if
            you leave this page.
          </p>
          <ol>
            {confirming.map((step) => (
              <li key={step}>{STEP_LABELS[step]}</li>
            ))}
          </ol>
        </Dialog>
      ) : null}
    </div>
  )
}
