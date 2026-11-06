import { Fragment, useEffect, useRef, useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { TextField } from '../../../components/ui/TextField'
import { api, ApiError } from '../../../lib/api'
import {
  EVENT_LEVELS,
  eventLogLogin,
  eventLogQuery,
  eventMatches,
  firstLine,
  TIME_RANGES,
  validEventId,
  type EventLevel,
  type EventLogEntry,
  type EventLogFilters,
  type EventLogInfo,
  type EventLogPage,
} from '../eventLog'
import type { RemoteSession, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar } from './SessionChrome'
import styles from './Session.module.css'

interface EventLogViewProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
}

const timeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'short', timeStyle: 'medium' })

function problemText(error: unknown, fallback: string): string {
  return error instanceof ApiError ? error.message : fallback
}

/** A dot in the level's colour: red for errors and failed audits, amber for warnings. */
function levelDot(level: EventLevel): string {
  return level === 'Error' || level === 'Audit Failure' ? styles.failed : level === 'Warning' ? styles.connecting : styles.connected
}

/**
 * A Windows host's event logs, newest first, over WMI with the host's RPC service and a login. Read
 * only. Choosing a log, time range or level reads again at once; source and event ID on Apply.
 */
export function EventLogView({ session, active, onShowHost }: EventLogViewProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)
  const [logs, setLogs] = useState<EventLogInfo[]>([])
  const [filters, setFilters] = useState<EventLogFilters>({ log: '', hours: 0, levels: [], source: '', eventId: '' })
  const [source, setSource] = useState('')
  const [eventId, setEventId] = useState('')
  const [reload, setReload] = useState(0)
  const [events, setEvents] = useState<EventLogEntry[]>([])
  const [hasMore, setHasMore] = useState(false)
  // The read that last finished; a different one from what's asked for means it's still reading.
  const [finished, setFinished] = useState<{ filters: EventLogFilters; reload: number } | null>(null)
  const [loadingOlder, setLoadingOlder] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<number | null>(null)
  const [find, setFind] = useState('')
  // Ignores a page of older events that arrives after the filters changed.
  const readId = useRef(0)
  const target = session.title

  useEffect(() => {
    let cancelled = false
    api.post<{ logs: EventLogInfo[] }>('/api/eventlog/logs', { body: eventLogLogin(session.request) }).then(
      (response) => {
        if (cancelled) {
          return
        }
        setLogs(response.logs)
        setPhase({ kind: 'connected', target })
        setFilters((current) => ({
          ...current,
          log: response.logs.some((log) => log.name === current.log)
            ? current.log
            : (response.logs.find((log) => log.name === 'System') ?? response.logs[0])?.name ?? '',
        }))
      },
      (error: unknown) => {
        if (!cancelled) {
          setPhase({
            kind: 'ended',
            target,
            code: error instanceof ApiError ? error.code : undefined,
            message: problemText(error, 'Couldn’t read this host’s event logs.'),
          })
        }
      },
    )
    return () => {
      cancelled = true
    }
  }, [session.request, target, attempt])

  useEffect(() => {
    if (!filters.log) {
      return
    }
    let cancelled = false
    readId.current += 1
    api
      .post<EventLogPage>('/api/eventlog/events', { body: eventLogQuery(session.request, filters, new Date()) })
      .then(
        (page) => {
          if (!cancelled) {
            setEvents(page.events)
            setHasMore(page.hasMore)
            setProblem(null)
            setExpanded(null)
          }
        },
        (error: unknown) => {
          if (!cancelled) {
            setEvents([])
            setHasMore(false)
            setProblem(problemText(error, 'Couldn’t read this log.'))
          }
        },
      )
      .finally(() => {
        if (!cancelled) {
          setFinished({ filters, reload })
        }
      })
    return () => {
      cancelled = true
    }
  }, [session.request, filters, reload])

  function loadOlder() {
    const last = events.at(-1)
    if (!last) {
      return
    }
    const id = ++readId.current
    setLoadingOlder(true)
    setProblem(null)
    api
      .post<EventLogPage>('/api/eventlog/events', { body: eventLogQuery(session.request, filters, new Date(), last.recordNumber) })
      .then(
        (page) => {
          if (id === readId.current) {
            setEvents((current) => [...current, ...page.events])
            setHasMore(page.hasMore)
          }
        },
        (error: unknown) => {
          if (id === readId.current) {
            setProblem(problemText(error, 'Couldn’t read older events.'))
          }
        },
      )
      .finally(() => setLoadingOlder(false))
  }

  function reconnect() {
    setPhase({ kind: 'connecting', target })
    setAttempt((n) => n + 1)
  }

  function apply(event: FormEvent) {
    event.preventDefault()
    if (validEventId(eventId)) {
      setFilters({ ...filters, source, eventId })
    }
  }

  function toggleLevel(level: EventLevel) {
    setFilters({ ...filters, levels: filters.levels.includes(level) ? filters.levels.filter((l) => l !== level) : [...filters.levels, level] })
  }

  const loading = filters.log !== '' && (finished?.filters !== filters || finished.reload !== reload)
  const shown = events.filter((event) => eventMatches(event, find))
  const securityHidden = logs.length > 0 && !logs.some((log) => log.name === 'Security')
  const emptySecurity = filters.log === 'Security' && !loading && !problem && events.length === 0

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} event log`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'connected' ? (
          <Button variant="ghost" size="sm" onClick={() => setReload((n) => n + 1)} disabled={loading || loadingOlder || !filters.log}>
            {loading ? 'Reading…' : 'Refresh'}
          </Button>
        ) : null}
      </SessionStatusBar>

      <div className={`${styles.terminalArea} ${styles.webArea}`}>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={reconnect} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
        {phase.kind === 'connected' ? (
          <div className={styles.vmList}>
            <form className={styles.eventFilters} onSubmit={apply}>
              <label className={styles.eventField}>
                Log
                <select className={styles.eventSelect} value={filters.log} onChange={(event) => setFilters({ ...filters, log: event.target.value })} disabled={logs.length === 0}>
                  {logs.map((log) => (
                    <option key={log.name} value={log.name}>
                      {log.name} ({log.records.toLocaleString()})
                    </option>
                  ))}
                </select>
              </label>
              <label className={styles.eventField}>
                Time
                <select className={styles.eventSelect} value={filters.hours} onChange={(event) => setFilters({ ...filters, hours: Number(event.target.value) })}>
                  {TIME_RANGES.map((range) => (
                    <option key={range.hours} value={range.hours}>
                      {range.label}
                    </option>
                  ))}
                </select>
              </label>
              <TextField label="Source" value={source} onChange={(event) => setSource(event.target.value)} maxLength={256} spellCheck={false} placeholder="Any" />
              <TextField
                label="Event ID"
                value={eventId}
                onChange={(event) => setEventId(event.target.value)}
                inputMode="numeric"
                placeholder="Any"
                error={validEventId(eventId) ? undefined : 'Use 0 to 65535.'}
              />
              <Button type="submit" size="sm" variant="secondary" disabled={!validEventId(eventId) || !filters.log}>
                Apply
              </Button>
            </form>
            <fieldset className={styles.eventLevels}>
              <legend className="visually-hidden">Levels (none for all)</legend>
              {EVENT_LEVELS.map((level) => (
                <Checkbox key={level} label={level} checked={filters.levels.includes(level)} onChange={() => toggleLevel(level)} />
              ))}
            </fieldset>

            {logs.length === 0 ? <Alert variant="info">Windows lists no event logs that this login can read.</Alert> : null}
            {securityHidden || emptySecurity ? (
              <p className={styles.hint}>
                {securityHidden ? 'The Security log isn’t listed: ' : 'If the Security log should have events: '}
                the login needs the “Manage auditing and security log” right on this host (administrators have it).
              </p>
            ) : null}
            {problem && !loading ? <Alert variant="error">{problem}</Alert> : null}

            {filters.log && !problem && !loading && events.length === 0 ? (
              filters.hours > 0 ? (
                <p className={styles.vmEmpty}>
                  No events match in this time range. Older ones may.{' '}
                  <Button size="sm" variant="secondary" onClick={() => setFilters({ ...filters, hours: 0 })}>
                    Search all time
                  </Button>
                </p>
              ) : (
                <p className={styles.vmEmpty}>No events match.</p>
              )
            ) : null}
            {loading && events.length === 0 ? <p className={styles.vmEmpty}>Reading {filters.log}…</p> : null}

            {events.length > 0 ? (
              <>
                <div className={styles.eventFind}>
                  <TextField label="Find in the events below" hideLabel placeholder="Find in the events below" value={find} onChange={(event) => setFind(event.target.value)} />
                  <span className={styles.hint} role="status">
                    {find.trim() ? `${shown.length} of ${events.length} events` : `${events.length} events`}
                    {hasMore ? ', more are older' : ''}
                  </span>
                </div>
                <table className={styles.vmTable}>
                  <thead>
                    <tr>
                      <th scope="col">Level</th>
                      <th scope="col">Time</th>
                      <th scope="col">Source</th>
                      <th scope="col">Event ID</th>
                      <th scope="col">Message</th>
                    </tr>
                  </thead>
                  <tbody>
                    {shown.map((event) => {
                      const open = expanded === event.recordNumber
                      return (
                        <Fragment key={event.recordNumber}>
                          <tr>
                            <td className={styles.eventLevel}>
                              <span className={`${styles.dot} ${levelDot(event.level)}`} /> {event.level}
                            </td>
                            <td className={styles.eventTime}>{event.timeGenerated ? timeFormat.format(new Date(event.timeGenerated)) : '—'}</td>
                            <td>{event.source ?? '—'}</td>
                            <td>{event.eventId}</td>
                            <td className={styles.eventMessage}>
                              <button
                                type="button"
                                className={styles.eventToggle}
                                aria-expanded={open}
                                onClick={() => setExpanded(open ? null : event.recordNumber)}
                              >
                                {firstLine(event.message)}
                              </button>
                            </td>
                          </tr>
                          {open ? (
                            <tr>
                              <td colSpan={5} className={styles.eventDetails}>
                                <pre>{event.message}</pre>
                                <dl>
                                  <div>
                                    <dt>Record</dt>
                                    <dd>{event.recordNumber}</dd>
                                  </div>
                                  {event.category ? (
                                    <div>
                                      <dt>Task category</dt>
                                      <dd>{event.category}</dd>
                                    </div>
                                  ) : null}
                                  {event.user ? (
                                    <div>
                                      <dt>User</dt>
                                      <dd>{event.user}</dd>
                                    </div>
                                  ) : null}
                                  {event.computer ? (
                                    <div>
                                      <dt>Computer</dt>
                                      <dd>{event.computer}</dd>
                                    </div>
                                  ) : null}
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
                  <div className={styles.eventMore}>
                    <Button size="sm" variant="secondary" onClick={loadOlder} loading={loadingOlder} disabled={loading}>
                      Load older events
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
