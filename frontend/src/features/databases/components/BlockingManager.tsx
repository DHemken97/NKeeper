import { Fragment, useCallback, useEffect, useState, type KeyboardEvent, type MouseEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { ContextMenu, type MenuEntry } from '../../../components/ui/ContextMenu'
import { Dialog } from '../../../components/ui/Dialog'
import { ApiError } from '../../../lib/api'
import { formatElapsed } from '../format'
import { useDbApi, useDbEngineInfo } from '../engineContext'
import { type BlockingReport, type BlockingSession } from '../databaseApi'
import styles from './Database.module.css'

interface BlockingManagerProps {
  /** The database tab's own session (the blocking manager reads and kills on a short connection of its own). */
  sessionId: string
  active: boolean
  /** Opens a session's SQL in a new query window. */
  onOpenQuery: (sql: string) => void
}

const REFRESH_CHOICES = [
  { seconds: 0, label: 'Off' },
  { seconds: 5, label: '5 s' },
  { seconds: 10, label: '10 s' },
  { seconds: 30, label: '30 s' },
]

/** A row drawn in the table: a session at its depth in its chain. */
interface Row {
  session: BlockingSession
  depth: number
}

function flatten(sessions: BlockingSession[], expanded: ReadonlySet<number>, depth = 0, into: Row[] = []): Row[] {
  for (const session of sessions) {
    into.push({ session, depth })
    if (expanded.has(session.sessionId)) {
      flatten(session.blockedSessions, expanded, depth + 1, into)
    }
  }
  return into
}

/** Every session in the chains that blocks others, to expand them all. */
function blockers(sessions: BlockingSession[], into: number[] = []): number[] {
  for (const session of sessions) {
    if (session.blockedSessions.length > 0) {
      into.push(session.sessionId)
      blockers(session.blockedSessions, into)
    }
  }
  return into
}

function oneLine(sql: string | null): string {
  return (sql ?? '').replace(/\s+/g, ' ').trim()
}

/**
 * The blocking manager: each blocking chain under its lead blocker (the session at its root, which
 * blocks others but waits on no one), its blocked sessions collapsed beneath it, and any session
 * blocking only itself. Right-click a row (or Shift+F10) to kill that session, after confirming.
 */
export function BlockingManager({ sessionId, active, onOpenQuery }: BlockingManagerProps) {
  const db = useDbApi()
  const engine = useDbEngineInfo()
  const [report, setReport] = useState<BlockingReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<Set<number>>(new Set())
  const [refreshEvery, setRefreshEvery] = useState(10)
  const [menu, setMenu] = useState<{ x: number; y: number; session: BlockingSession } | null>(null)
  const [killing, setKilling] = useState<BlockingSession | null>(null)
  const [killBusy, setKillBusy] = useState(false)
  const [killError, setKillError] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    try {
      setReport(await db.blocking(sessionId))
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Couldn’t read the blocking chains.')
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

  useEffect(() => {
    if (!active || refreshEvery === 0) {
      return
    }
    const timer = window.setInterval(() => void refresh(), refreshEvery * 1000)
    return () => window.clearInterval(timer)
  }, [active, refreshEvery, refresh])

  function toggle(id: number) {
    setExpanded((current) => {
      const next = new Set(current)
      if (next.has(id)) {
        next.delete(id)
      } else {
        next.add(id)
      }
      return next
    })
  }

  async function kill(session: BlockingSession) {
    setKillBusy(true)
    setKillError(null)
    try {
      await db.kill(sessionId, session.sessionId, session.serial)
      setKilling(null)
      setNotice(`Session ${session.sessionId} was killed. Its open work is being rolled back.`)
      await refresh()
    } catch (e) {
      setKillError(e instanceof ApiError ? e.message : `Couldn’t kill session ${session.sessionId}.`)
    } finally {
      setKillBusy(false)
    }
  }

  function menuEntries(session: BlockingSession): MenuEntry[] {
    const sql = session.sql
    return [
      { kind: 'header', label: `Session ${session.sessionId}` },
      {
        kind: 'item',
        label: `Kill session ${session.sessionId}…`,
        detail: session.placeholder ? 'Not a session' : undefined,
        disabled: session.placeholder || session.sessionId <= 0,
        onSelect: () => {
          setKillError(null)
          setKilling(session)
        },
      },
      { kind: 'separator' },
      { kind: 'item', label: 'Open SQL in a new query', disabled: !sql, onSelect: () => sql && onOpenQuery(sql) },
      { kind: 'item', label: 'Copy SQL', disabled: !sql, onSelect: () => sql && void navigator.clipboard?.writeText(sql).catch(() => {}) },
      { kind: 'item', label: 'Copy session ID', onSelect: () => void navigator.clipboard?.writeText(String(session.sessionId)).catch(() => {}) },
    ]
  }

  function openMenu(event: MouseEvent, session: BlockingSession) {
    event.preventDefault()
    setMenu({ x: event.clientX, y: event.clientY, session })
  }

  function onRowKeyDown(event: KeyboardEvent<HTMLTableRowElement>, session: BlockingSession) {
    if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
      event.preventDefault()
      const rect = event.currentTarget.getBoundingClientRect()
      setMenu({ x: rect.left + 40, y: rect.bottom, session })
    } else if ((event.key === 'Enter' || event.key === ' ') && session.blockedSessions.length > 0) {
      event.preventDefault()
      toggle(session.sessionId)
    } else if (event.key === 'Delete' && !session.placeholder) {
      event.preventDefault()
      setKillError(null)
      setKilling(session)
    }
  }

  const leads = report?.leads ?? []
  const rows = flatten(leads, expanded)
  const chains = leads.filter((lead) => lead.blockedSessions.length > 0).length
  const selfOnly = leads.length - chains

  return (
    <div className={styles.toolPage} hidden={!active} role="tabpanel" aria-label="Blocking Manager">
      <header className={styles.toolHeader}>
        <div>
          <h2>Lead Blockers</h2>
          <p>Sessions at the Root of Each Blocking Chain, and Sessions Blocking Themselves</p>
        </div>
        <div className={styles.toolActions}>
          <span className={styles.toolbarNote}>
            {report
              ? `${chains} ${chains === 1 ? 'chain' : 'chains'} · ${selfOnly > 0 ? `${selfOnly} self-blocking · ` : ''}${report.blockedCount} blocked · ${new Date(report.atUtc).toLocaleTimeString()}`
              : null}
          </span>
          <label className={styles.toolbarField}>
            <span>Auto-refresh</span>
            <select value={refreshEvery} onChange={(event) => setRefreshEvery(Number(event.target.value))} aria-label="Auto-refresh">
              {REFRESH_CHOICES.map((choice) => (
                <option key={choice.seconds} value={choice.seconds}>
                  {choice.label}
                </option>
              ))}
            </select>
          </label>
          <Button size="sm" variant="secondary" onClick={() => setExpanded(new Set(blockers(leads)))} disabled={leads.length === 0}>
            Expand all
          </Button>
          <Button size="sm" variant="secondary" onClick={() => setExpanded(new Set())} disabled={expanded.size === 0}>
            Collapse all
          </Button>
          <Button size="sm" onClick={() => void refresh()} loading={loading}>
            Refresh
          </Button>
        </div>
      </header>

      {notice ? <Alert variant="info">{notice}</Alert> : null}
      {error ? <Alert variant="error">{error}</Alert> : null}
      {report && !report.canSeeAllSessions ? (
        <Alert variant="error">This login lacks VIEW SERVER STATE, so only its own sessions are shown. Blocking by other sessions may be missing.</Alert>
      ) : null}

      {report && leads.length === 0 ? <p className={styles.blockingEmpty}>No blocking right now.</p> : null}

      {leads.length > 0 ? (
        <div className={styles.toolTableWrap}>
          <table className={styles.toolTable} aria-label="Blocking chains">
            <thead>
              <tr>
                <th scope="col">Session</th>
                <th scope="col">Login</th>
                <th scope="col">Host</th>
                <th scope="col">Program</th>
                <th scope="col">Database</th>
                <th scope="col">Status</th>
                <th scope="col" className={styles.numeric}>
                  Open Txns
                </th>
                <th scope="col" className={styles.numeric}>
                  Blocked
                </th>
                <th scope="col" className={styles.numeric}>
                  Blocked Volume
                </th>
                <th scope="col">Current SQL</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(({ session, depth }) => {
                const lead = depth === 0
                const canExpand = session.blockedSessions.length > 0
                const open = expanded.has(session.sessionId)
                return (
                  <Fragment key={`${depth}-${session.sessionId}`}>
                    <tr
                      className={lead ? styles.leadRow : styles.blockedRow}
                      tabIndex={0}
                      aria-level={depth + 1}
                      aria-expanded={canExpand ? open : undefined}
                      onContextMenu={(event) => openMenu(event, session)}
                      onKeyDown={(event) => onRowKeyDown(event, session)}
                    >
                      <td className={styles.sessionCell} style={{ paddingLeft: `${depth * 20 + 8}px` }}>
                        {canExpand ? (
                          <button
                            type="button"
                            className={styles.chainToggle}
                            onClick={() => toggle(session.sessionId)}
                            aria-label={`${open ? 'Collapse' : 'Expand'} the sessions ${session.sessionId} blocks`}
                          >
                            {open ? '▾' : '▸'}
                          </button>
                        ) : (
                          <span className={styles.chainSpacer} />
                        )}
                        <span className={styles.spid}>{session.sessionId}</span>
                        {lead && canExpand ? <span className={styles.leadBadge}>LEAD</span> : null}
                        {session.selfBlocking ? (
                          <span className={styles.selfBadge} title="This session is waiting on itself, usually one of its own parallel threads or its own I/O.">
                            SELF
                          </span>
                        ) : null}
                      </td>
                      <td>{session.login ?? '—'}</td>
                      <td>{session.host ?? '—'}</td>
                      <td className={styles.ellipsis} title={session.program ?? undefined}>
                        {session.program ?? '—'}
                      </td>
                      <td>{session.database ?? 'N/A'}</td>
                      <td>
                        {session.status ?? '—'}
                        {session.waitType ? (
                          <span className={styles.waitInfo} title={session.waitResource ?? undefined}>
                            {session.waitType}
                            {session.waitTimeMs != null ? ` · ${formatElapsed(session.waitTimeMs)}` : ''}
                          </span>
                        ) : null}
                      </td>
                      <td className={styles.numeric}>{session.openTransactions}</td>
                      <td className={styles.numeric}>{session.blocked}</td>
                      <td className={styles.numeric}>{session.blockedVolume}</td>
                      <td className={`${styles.ellipsis} ${styles.sqlCell}`} title={session.sql ?? undefined}>
                        {oneLine(session.sql)}
                      </td>
                    </tr>
                  </Fragment>
                )
              })}
            </tbody>
          </table>
          <p className={styles.hint}>Right-click a session (or press Shift+F10) to kill it, or to open its SQL.</p>
        </div>
      ) : null}

      {menu ? (
        <ContextMenu label={`Actions for session ${menu.session.sessionId}`} entries={menuEntries(menu.session)} x={menu.x} y={menu.y} onClose={() => setMenu(null)} />
      ) : null}

      {killing ? (
        <Dialog
          title={`Kill session ${killing.sessionId}?`}
          onClose={() => !killBusy && setKilling(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setKilling(null)} disabled={killBusy}>
                Cancel
              </Button>
              <Button variant="danger" onClick={() => void kill(killing)} loading={killBusy}>
                Kill session {killing.sessionId}
              </Button>
            </>
          }
        >
          <dl className={styles.killDetails}>
            <dt>Login</dt>
            <dd>{killing.login ?? '—'}</dd>
            <dt>Host</dt>
            <dd>{killing.host ?? '—'}</dd>
            <dt>Program</dt>
            <dd>{killing.program ?? '—'}</dd>
            <dt>Open transactions</dt>
            <dd>{killing.openTransactions}</dd>
            <dt>Blocking</dt>
            <dd>{killing.blockedVolume} {killing.blockedVolume === 1 ? 'session' : 'sessions'}</dd>
          </dl>
          <p>
            {engine.product} ends the session and rolls back its open transaction, which can take as long as the work it undoes. The application that owns it
            will see its connection drop.
          </p>
          {killError ? <Alert variant="error">{killError}</Alert> : null}
        </Dialog>
      ) : null}
    </div>
  )
}
