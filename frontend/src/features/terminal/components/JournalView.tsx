import { Fragment, useEffect, useRef, useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { TextField } from '../../../components/ui/TextField'
import { api, ApiError, apiWebSocketUrl } from '../../../lib/api'
import {
  firstLine,
  fromLocalInput,
  journalEntryMatches,
  journalFilterBody,
  PRIORITIES,
  rangeProblem,
  toLocalInput,
  type JournalEntry,
  type JournalFilters,
  type JournalPage,
  type JournalResponse,
} from '../journal'
import { hostKeyPrompt, kvmRequestBody } from '../kvm'
import type { RemoteSession, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import styles from './Session.module.css'

interface JournalViewProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
}

/** What a tab keeps while following: a busy host shouldn't fill the browser's memory. */
const MAX_ENTRIES = 5000

const timeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'short', timeStyle: 'medium' })

function problemText(error: unknown, fallback: string): string {
  return error instanceof ApiError ? error.message : fallback
}

function priorityDot(priority: number | null): string {
  return priority === null || priority > 4 ? styles.connected : priority === 4 ? styles.connecting : styles.failed
}

function initialFilters(): JournalFilters {
  return { start: toLocalInput(new Date(Date.now() - 3_600_000)), stop: '', maxPriority: '', unit: '', identifier: '', search: '' }
}

/**
 * A Linux host's systemd journal over SSH, newest first: filtered by start and stop time, priority,
 * unit, program and message, paged back, and followed live. Read only.
 */
export function JournalView({ session, active, onShowHost }: JournalViewProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [draft, setDraft] = useState<JournalFilters>(initialFilters)
  const [filters, setFilters] = useState(draft)
  const [attempt, setAttempt] = useState(0)
  const [finished, setFinished] = useState<{ filters: JournalFilters; attempt: number } | null>(null)
  const [entries, setEntries] = useState<JournalEntry[]>([])
  const [hasMore, setHasMore] = useState(false)
  const [limited, setLimited] = useState(false)
  const [loadingOlder, setLoadingOlder] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [find, setFind] = useState('')
  const [following, setFollowing] = useState(false)
  const trustRef = useRef<string | undefined>(undefined)
  const newestRef = useRef<string | undefined>(undefined)
  const readId = useRef(0)
  const connectedRef = useRef(false)
  const target = session.title
  const loading = finished?.filters !== filters || finished.attempt !== attempt

  useEffect(() => {
    let cancelled = false
    readId.current += 1
    const body = {
      ...kvmRequestBody(session.request, trustRef.current),
      ...journalFilterBody(filters),
      since: fromLocalInput(filters.start),
      until: fromLocalInput(filters.stop),
    }
    api
      .post<JournalResponse>('/api/journal/entries', { body })
      .then(
        (response) => {
          if (cancelled) {
            return
          }
          if (response.status === 'trustRequired' && response.hostKey) {
            setPhase({ kind: 'hostKey', prompt: hostKeyPrompt(response.hostKey), target })
            return
          }
          const page = response.page as JournalPage
          setEntries(page.entries)
          setHasMore(page.hasMore)
          setLimited(page.limited)
          setProblem(null)
          setExpanded(null)
          connectedRef.current = true
          setPhase({ kind: 'connected', target })
        },
        (error: unknown) => {
          if (cancelled) {
            return
          }
          setEntries([])
          setHasMore(false)
          const message = problemText(error, 'Couldn’t read the journal.')
          if (connectedRef.current) {
            // The tab keeps its filters, so the query can be changed (e.g. a bad regular expression).
            setProblem(message)
          } else {
            setPhase({ kind: 'ended', target, code: error instanceof ApiError ? error.code : undefined, message })
          }
        },
      )
      .finally(() => {
        if (!cancelled) {
          setFinished({ filters, attempt })
        }
      })
    return () => {
      cancelled = true
    }
  }, [session.request, target, filters, attempt])

  const newest = entries[0]?.cursor
  useEffect(() => {
    newestRef.current = newest
  }, [newest])

  useEffect(() => {
    // Starts from what's shown, so nothing falls between the read and the follow.
    if (!following || loading) {
      return
    }
    const request = session.request
    const socket = new WebSocket(apiWebSocketUrl('/api/journal/follow'))
    socket.onopen = () => {
      socket.send(
        JSON.stringify({
          type: 'start',
          hostId: request.hostId,
          serviceIndex: request.serviceIndex,
          credentialId: request.credentialId,
          username: request.oneOff?.username,
          password: request.oneOff?.password,
          ...journalFilterBody(filters),
          after: newestRef.current,
        }),
      )
    }
    socket.onmessage = (event: MessageEvent<string>) => {
      const message = JSON.parse(event.data) as { type: string; entry?: JournalEntry; message?: string }
      if (message.type === 'entry' && message.entry) {
        const entry = message.entry
        setEntries((current) => [entry, ...current].slice(0, MAX_ENTRIES))
      } else if (message.type === 'error' || message.type === 'closed') {
        setFollowing(false)
        if (message.type === 'error') {
          setProblem(message.message ?? 'Following the journal stopped.')
        }
      }
    }
    socket.onclose = () => setFollowing(false)
    return () => {
      socket.onclose = null
      socket.onmessage = null
      socket.close()
    }
  }, [following, loading, filters, session.request])

  function decide(trust: boolean) {
    if (phase.kind !== 'hostKey') {
      return
    }
    if (!trust) {
      setPhase({ kind: 'ended', target, code: 'host_key_rejected', message: 'The host key was not trusted, so NKeeper didn’t connect.' })
      return
    }
    trustRef.current = phase.prompt.fingerprint
    setPhase({ kind: 'connecting', target })
    setAttempt((n) => n + 1)
  }

  function refresh() {
    if (phase.kind !== 'connected') {
      setPhase({ kind: 'connecting', target })
    }
    setAttempt((n) => n + 1)
  }

  function apply(event: FormEvent) {
    event.preventDefault()
    if (rangeProblem(draft.start, draft.stop) === null) {
      setFollowing(false)
      setFilters(draft)
    }
  }

  function loadOlder() {
    const last = entries.at(-1)
    if (!last) {
      return
    }
    const id = ++readId.current
    setLoadingOlder(true)
    const body = {
      ...kvmRequestBody(session.request, trustRef.current),
      ...journalFilterBody(filters),
      since: fromLocalInput(filters.start),
      until: fromLocalInput(filters.stop),
      after: last.cursor,
    }
    api
      .post<JournalResponse>('/api/journal/entries', { body })
      .then(
        (response) => {
          if (id === readId.current && response.page) {
            const page = response.page
            setEntries((current) => [...current, ...page.entries].slice(0, MAX_ENTRIES))
            setHasMore(page.hasMore)
          }
        },
        (error: unknown) => {
          if (id === readId.current) {
            setProblem(problemText(error, 'Couldn’t read older entries.'))
          }
        },
      )
      .finally(() => setLoadingOlder(false))
  }

  const range = rangeProblem(draft.start, draft.stop)
  const shown = entries.filter((entry) => journalEntryMatches(entry, find))
  const set = (changes: Partial<JournalFilters>) => setDraft((current) => ({ ...current, ...changes }))

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} journal`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'connected' ? (
          <>
            <Button
              variant={following ? 'secondary' : 'ghost'}
              size="sm"
              onClick={() => setFollowing((f) => !f)}
              disabled={loading || filters.stop !== ''}
              title={filters.stop !== '' ? 'Following shows new entries, so it needs an empty Stop.' : undefined}
              aria-pressed={following}
            >
              {following ? 'Following…' : 'Follow'}
            </Button>
            <Button variant="ghost" size="sm" onClick={refresh} disabled={loading || loadingOlder}>
              {loading ? 'Reading…' : 'Refresh'}
            </Button>
          </>
        ) : null}
      </SessionStatusBar>

      <div className={`${styles.terminalArea} ${styles.webArea}`}>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={refresh} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
        {phase.kind === 'connected' ? (
          <div className={styles.vmList}>
            <form className={styles.logFilters} onSubmit={apply}>
              <TextField label="Start" type="datetime-local" step={60} value={draft.start} onChange={(event) => set({ start: event.target.value })} />
              <TextField label="Stop" type="datetime-local" step={60} value={draft.stop} onChange={(event) => set({ stop: event.target.value })} error={range ?? undefined} />
              <label className={styles.logField}>
                Priority
                <select className={styles.logSelect} value={draft.maxPriority} onChange={(event) => set({ maxPriority: event.target.value })}>
                  <option value="">Any</option>
                  {PRIORITIES.slice(0, 7).map((name, value) => (
                    <option key={name} value={value}>
                      {name} and worse
                    </option>
                  ))}
                </select>
              </label>
              <TextField label="Unit" value={draft.unit} onChange={(event) => set({ unit: event.target.value })} maxLength={256} spellCheck={false} placeholder="sshd.service" />
              <TextField label="Program" value={draft.identifier} onChange={(event) => set({ identifier: event.target.value })} maxLength={256} spellCheck={false} placeholder="Any" />
              <TextField label="Search" value={draft.search} onChange={(event) => set({ search: event.target.value })} maxLength={1024} spellCheck={false} placeholder="Regular expression" />
              <Button type="submit" size="sm" variant="secondary" disabled={range !== null}>
                Apply
              </Button>
            </form>

            {limited ? <p className={styles.hint}>This login can only read part of the journal. Use root, a user who may use sudo without a password, or one in the systemd-journal group.</p> : null}
            {problem ? <Alert variant="error">{problem}</Alert> : null}
            {!problem && !loading && entries.length === 0 ? <p className={styles.vmEmpty}>No entries match.{following ? ' Waiting for new ones…' : ''}</p> : null}
            {loading && entries.length === 0 ? <p className={styles.vmEmpty}>Reading the journal…</p> : null}

            {entries.length > 0 ? (
              <>
                <div className={styles.logFind}>
                  <TextField label="Find in the entries below" hideLabel placeholder="Find in the entries below" value={find} onChange={(event) => setFind(event.target.value)} />
                  <span className={styles.hint} role="status">
                    {find.trim() ? `${shown.length} of ${entries.length} entries` : `${entries.length} entries`}
                    {hasMore ? ', more are older' : ''}
                  </span>
                </div>
                <table className={styles.vmTable}>
                  <thead>
                    <tr>
                      <th scope="col">Priority</th>
                      <th scope="col">Time</th>
                      <th scope="col">Program</th>
                      <th scope="col">Message</th>
                    </tr>
                  </thead>
                  <tbody>
                    {shown.map((entry) => {
                      const open = expanded === entry.cursor
                      return (
                        <Fragment key={entry.cursor}>
                          <tr>
                            <td className={styles.logNarrow}>
                              <span className={`${styles.dot} ${priorityDot(entry.priority)}`} /> {entry.priority === null ? '—' : (PRIORITIES[entry.priority] ?? entry.priority)}
                            </td>
                            <td className={styles.logNarrow}>{entry.time ? timeFormat.format(new Date(entry.time)) : '—'}</td>
                            <td className={styles.logNarrow}>{entry.identifier ?? entry.unit ?? '—'}</td>
                            <td className={styles.logMessage}>
                              <button type="button" className={styles.logToggle} aria-expanded={open} onClick={() => setExpanded(open ? null : entry.cursor)}>
                                {firstLine(entry.message) || '(empty)'}
                              </button>
                            </td>
                          </tr>
                          {open ? (
                            <tr>
                              <td colSpan={4} className={styles.logDetails}>
                                <pre>{entry.message}</pre>
                                <dl>
                                  {[
                                    ['Unit', entry.unit],
                                    ['PID', entry.pid],
                                    ['Host', entry.hostname],
                                  ].map(([label, value]) =>
                                    value ? (
                                      <div key={label}>
                                        <dt>{label}</dt>
                                        <dd>{value}</dd>
                                      </div>
                                    ) : null,
                                  )}
                                </dl>
                              </td>
                            </tr>
                          ) : null}
                        </Fragment>
                      )
                    })}
                  </tbody>
                </table>
                {hasMore ? (
                  <div className={styles.logMore}>
                    <Button size="sm" variant="secondary" onClick={loadOlder} loading={loadingOlder} disabled={loading}>
                      Load older entries
                    </Button>
                  </div>
                ) : null}
              </>
            ) : null}
          </div>
        ) : null}
      </div>
    </div>
  )
}
