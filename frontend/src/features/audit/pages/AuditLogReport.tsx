import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { useResource } from '../../inventory/hooks/useResource'
import { ACTION_LABELS, auditApi, CATEGORIES, type AuditEvent, type AuditFilters } from '../auditApi'
import reportStyles from '../../reports/pages/Reports.module.css'
import styles from './Audit.module.css'

const timeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' })

/**
 * The audit trail (administrators): sign-ins, sessions, changes to hosts and users, file transfers
 * and moves, newest first, with filters. Terminal sessions link to their recording.
 */
export function AuditLogReport() {
  const [filters, setFilters] = useState<AuditFilters>({})
  const [search, setSearch] = useState('')
  const key = JSON.stringify(filters)
  const first = useResource((signal) => auditApi.events(filters, undefined, signal), key)
  // Older pages loaded with "Load older", for the current filters only.
  const [more, setMore] = useState<{ key: string; events: AuditEvent[]; next: number | null } | null>(null)
  const [loadingMore, setLoadingMore] = useState(false)
  const [moreError, setMoreError] = useState<unknown>(null)
  const extra = more?.key === key ? more : null
  const events = first.data ? [...first.data.events, ...(extra?.events ?? [])] : null
  const next = extra ? extra.next : (first.data?.next ?? null)
  const error = first.error ?? moreError

  // Typing settles for a moment before the search runs.
  useEffect(() => {
    const timer = setTimeout(() => setFilters((current) => ((current.q ?? '') === search ? current : { ...current, q: search })), 300)
    return () => clearTimeout(timer)
  }, [search])

  async function loadMore() {
    if (next === null) {
      return
    }
    setLoadingMore(true)
    setMoreError(null)
    try {
      const page = await auditApi.events(filters, next)
      setMore({ key, events: [...(extra?.events ?? []), ...page.events], next: page.next })
    } catch (err) {
      setMoreError(err)
    } finally {
      setLoadingMore(false)
    }
  }

  return (
    <>
      <header className={reportStyles.reportHeader}>
        <div>
          <h2 className={reportStyles.reportTitle}>Audit log</h2>
          <p className={reportStyles.reportSummary}>Sign-ins, sessions and changes, newest first. Terminal sessions can be played back.</p>
        </div>
        <div className={reportStyles.reportActions}>
          <a className={styles.download} href={auditApi.csvUrl(filters)} download>
            Download CSV
          </a>
        </div>
      </header>

      <div className={styles.filters} role="search">
        <input
          className={styles.input}
          type="search"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          placeholder="Search user, host or detail"
          aria-label="Search the audit log"
        />
        <select
          className={styles.input}
          value={filters.category ?? ''}
          onChange={(event) => setFilters({ ...filters, category: event.target.value || undefined })}
          aria-label="What happened"
        >
          {CATEGORIES.map((c) => (
            <option key={c.value} value={c.value}>
              {c.label}
            </option>
          ))}
        </select>
        <label className={styles.date}>
          From
          <input className={styles.input} type="date" value={filters.from ?? ''} onChange={(event) => setFilters({ ...filters, from: event.target.value || undefined })} />
        </label>
        <label className={styles.date}>
          To
          <input className={styles.input} type="date" value={filters.to ?? ''} onChange={(event) => setFilters({ ...filters, to: event.target.value || undefined })} />
        </label>
      </div>

      {error && !events ? (
        <div className={reportStyles.state}>
          <Alert>{toFormErrors(error).message}</Alert>
        </div>
      ) : !events ? (
        <div className={reportStyles.state}>
          <Spinner label="Loading the audit log" />
        </div>
      ) : events.length === 0 ? (
        <p className={reportStyles.state}>Nothing matches.</p>
      ) : (
        <div className={reportStyles.tableScroll}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th scope="col">Time</th>
                <th scope="col">User</th>
                <th scope="col">What happened</th>
                <th scope="col">Host</th>
                <th scope="col">Detail</th>
                <th scope="col">From</th>
              </tr>
            </thead>
            <tbody>
              {events.map((event) => (
                <tr key={event.id} className={event.action.endsWith('failed') ? styles.failed : undefined}>
                  <td className={styles.time}>{timeFormat.format(new Date(event.occurredAtUtc))}</td>
                  <td>{event.userName ?? '—'}</td>
                  <td>{ACTION_LABELS[event.action] ?? event.action}</td>
                  <td>
                    {event.hostId && event.hostName ? (
                      <Link to={`/?host=${event.hostId}`} className={reportStyles.hostLink}>
                        {event.hostName}
                      </Link>
                    ) : (
                      (event.hostName ?? '')
                    )}
                  </td>
                  <td className={styles.detail}>
                    {event.detail}
                    {event.recordingId ? (
                      <>
                        {' '}
                        <Link to={`recordings/${event.recordingId}`} className={styles.play}>
                          ▶ Play recording
                        </Link>
                      </>
                    ) : null}
                  </td>
                  <td className={styles.client}>{event.clientAddress}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {next !== null ? (
            <div className={styles.more}>
              <Button variant="secondary" size="sm" loading={loadingMore} onClick={() => void loadMore()}>
                Load older
              </Button>
            </div>
          ) : null}
        </div>
      )}
    </>
  )
}
