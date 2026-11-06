import { Fragment, useEffect, useRef, useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { TextArea } from '../../../components/ui/TextArea'
import { TextField } from '../../../components/ui/TextField'
import { api, ApiError } from '../../../lib/api'
import { fromLocalInput, rangeProblem, toLocalInput } from '../journal'
import { hostKeyPrompt, kvmRequestBody } from '../kvm'
import {
  declaredModule,
  denialMatches,
  DISTRIBUTION_PRIORITY,
  MODULE_NAME,
  NEW_POLICY,
  suggestedName,
  type SelinuxDenial,
  type SelinuxModule,
  type SelinuxResponse,
  type SelinuxStatus,
} from '../selinux'
import type { RemoteSession, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import styles from './Session.module.css'

interface SelinuxViewProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
}

const timeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'short', timeStyle: 'medium' })

function problemText(error: unknown, fallback: string): string {
  return error instanceof ApiError ? error.message : fallback
}

/** The policy being written: its name first when it comes from denials, then its source to read. */
type Editing = { step: 'name'; name: string; denials: SelinuxDenial[] } | { step: 'source'; source: string }

/**
 * A Linux host's SELinux over SSH, in one place: its mode, what it has blocked, and the policy modules
 * installed. Policies are added by writing one, or from denials with audit2allow, and always shown
 * before they're installed.
 */
export function SelinuxView({ session, active, onShowHost }: SelinuxViewProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [selinux, setSelinux] = useState<SelinuxStatus | null>(null)
  const [refreshing, setRefreshing] = useState(true)
  const [attempt, setAttempt] = useState(0)
  const [start, setStart] = useState(() => toLocalInput(new Date(Date.now() - 24 * 3_600_000)))
  const [stop, setStop] = useState('')
  const [range, setRange] = useState({ start, stop })
  const [find, setFind] = useState('')
  const [selected, setSelected] = useState<ReadonlySet<number>>(new Set())
  const [expanded, setExpanded] = useState<number | null>(null)
  const [showDistribution, setShowDistribution] = useState(false)
  const [editing, setEditing] = useState<Editing | null>(null)
  const [removing, setRemoving] = useState<SelinuxModule | null>(null)
  const [busy, setBusy] = useState(false)
  const [dialogProblem, setDialogProblem] = useState<string | null>(null)
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null)
  const trustRef = useRef<string | undefined>(undefined)
  const target = session.title

  useEffect(() => {
    let cancelled = false
    api
      .post<SelinuxResponse>('/api/selinux/status', {
        body: { ...kvmRequestBody(session.request, trustRef.current), since: fromLocalInput(range.start), until: fromLocalInput(range.stop) },
      })
      .then(
        (response) => {
          if (cancelled) {
            return
          }
          if (response.status === 'trustRequired' && response.hostKey) {
            setPhase({ kind: 'hostKey', prompt: hostKeyPrompt(response.hostKey), target })
            return
          }
          setSelinux(response.selinux)
          setSelected(new Set())
          setPhase({ kind: 'connected', target })
        },
        (error: unknown) => {
          if (!cancelled) {
            setPhase({
              kind: 'ended',
              target,
              code: error instanceof ApiError ? error.code : undefined,
              message: problemText(error, 'Couldn’t read this host’s SELinux.'),
            })
          }
        },
      )
      .finally(() => {
        if (!cancelled) {
          setRefreshing(false)
        }
      })
    return () => {
      cancelled = true
    }
  }, [session.request, target, range, attempt])

  function decide(trust: boolean) {
    if (phase.kind !== 'hostKey') {
      return
    }
    if (!trust) {
      setPhase({ kind: 'ended', target, code: 'host_key_rejected', message: 'The host key was not trusted, so NKeeper didn’t connect.' })
      return
    }
    trustRef.current = phase.prompt.fingerprint
    refresh()
  }

  function refresh() {
    setRefreshing(true)
    if (phase.kind !== 'connected') {
      setPhase({ kind: 'connecting', target })
    }
    setAttempt((n) => n + 1)
  }

  function applyRange(event: FormEvent) {
    event.preventDefault()
    if (rangeProblem(start, stop) === null) {
      setRefreshing(true)
      setRange({ start, stop })
    }
  }

  function toggle(eventId: number) {
    setSelected((current) => {
      const next = new Set(current)
      if (!next.delete(eventId)) {
        next.add(eventId)
      }
      return next
    })
  }

  function openFromDenials() {
    const denials = (selinux?.denials ?? []).filter((d) => selected.has(d.eventId))
    setDialogProblem(null)
    setEditing({ step: 'name', name: suggestedName(denials), denials })
  }

  function openNew() {
    setDialogProblem(null)
    setEditing({ step: 'source', source: NEW_POLICY })
  }

  function generate(event: FormEvent) {
    event.preventDefault()
    if (editing?.step !== 'name' || !MODULE_NAME.test(editing.name.trim())) {
      return
    }
    const eventIds = [...new Set(editing.denials.map((d) => d.eventId))]
    setBusy(true)
    setDialogProblem(null)
    api
      .post<SelinuxResponse>('/api/selinux/generate', { body: { ...kvmRequestBody(session.request, trustRef.current), name: editing.name.trim(), eventIds } })
      .then(
        (response) => setEditing({ step: 'source', source: response.source ?? '' }),
        (error: unknown) => setDialogProblem(problemText(error, 'Couldn’t generate a policy.')),
      )
      .finally(() => setBusy(false))
  }

  function install(event: FormEvent) {
    event.preventDefault()
    if (editing?.step !== 'source') {
      return
    }
    setBusy(true)
    setDialogProblem(null)
    api
      .post<SelinuxResponse>('/api/selinux/install', { body: { ...kvmRequestBody(session.request, trustRef.current), source: editing.source } })
      .then(
        (response) => {
          setEditing(null)
          setResult({ ok: true, message: response.message ?? 'Installed.' })
          refresh()
        },
        (error: unknown) => setDialogProblem(problemText(error, 'Couldn’t install the policy.')),
      )
      .finally(() => setBusy(false))
  }

  function remove() {
    if (!removing) {
      return
    }
    const module = removing
    setBusy(true)
    setRemoving(null)
    setResult(null)
    api
      .post<SelinuxResponse>('/api/selinux/remove', { body: { ...kvmRequestBody(session.request, trustRef.current), name: module.name, priority: module.priority } })
      .then(
        (response) => {
          setResult({ ok: true, message: response.message ?? 'Removed.' })
          refresh()
        },
        (error: unknown) => setResult({ ok: false, message: problemText(error, `Couldn’t remove ${module.name}.`) }),
      )
      .finally(() => setBusy(false))
  }

  const rangeError = rangeProblem(start, stop)
  const denials = (selinux?.denials ?? []).filter((d) => denialMatches(d, find))
  const blocked = denials.filter((d) => d.blocked).length
  const local = (selinux?.modules ?? []).filter((m) => m.priority !== DISTRIBUTION_PRIORITY)
  const distribution = (selinux?.modules ?? []).filter((m) => m.priority === DISTRIBUTION_PRIORITY)
  const newName = editing?.step === 'source' ? declaredModule(editing.source) : null
  const replaces = newName !== null && local.some((m) => m.name === newName)
  const clashes = newName !== null && distribution.some((m) => m.name === newName)

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} SELinux`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'connected' ? (
          <Button variant="ghost" size="sm" onClick={refresh} disabled={refreshing || busy}>
            {refreshing ? 'Refreshing…' : 'Refresh'}
          </Button>
        ) : null}
      </SessionStatusBar>

      <div className={`${styles.terminalArea} ${styles.webArea}`}>
        {phase.kind === 'connecting' ? <OpeningCard /> : null}
        {phase.kind === 'hostKey' ? <TrustCard prompt={phase.prompt} target={phase.target} onDecide={decide} /> : null}
        {phase.kind === 'ended' ? (
          <EndedCard message={phase.message} code={phase.code} canReconnect onReconnect={refresh} onShowHost={() => onShowHost(session.hostId)} />
        ) : null}
        {phase.kind === 'connected' && selinux ? (
          <div className={styles.vmList}>
            {result ? <Alert variant={result.ok ? 'info' : 'error'}>{result.message}</Alert> : null}
            {selinux.mode === null ? (
              <p className={styles.vmEmpty}>This host has no SELinux tools (getenforce). Debian and Ubuntu hosts usually use AppArmor instead.</p>
            ) : (
              <>
                <section className={styles.storageSection} aria-label="Status">
                  <header className={styles.storageHeader}>
                    <h2>
                      <span
                        className={`${styles.dot} ${selinux.mode === 'Enforcing' ? styles.connected : selinux.mode === 'Permissive' ? styles.connecting : styles.failed}`}
                      />{' '}
                      {selinux.mode}
                    </h2>
                    <span className={styles.hint}>
                      {selinux.mode === 'Enforcing'
                        ? 'SELinux blocks what its policy doesn’t allow.'
                        : selinux.mode === 'Permissive'
                          ? 'SELinux only logs what its policy doesn’t allow, without blocking it.'
                          : 'SELinux is off on this host, so its policies have no effect.'}
                    </span>
                  </header>
                </section>

                <section className={styles.storageSection} aria-label="Blocks and audits">
                  <header className={styles.storageHeader}>
                    <h2>Blocks and audits</h2>
                    <span className={styles.hint}>
                      {denials.length === 0 ? 'None' : `${blocked} blocked, ${denials.length - blocked} logged only`}
                    </span>
                    <Button size="sm" variant="secondary" onClick={openFromDenials} disabled={selected.size === 0 || busy}>
                      {`Create policy from selected${selected.size > 0 ? ` (${selected.size})` : ''}`}
                    </Button>
                  </header>
                  <form className={styles.logFilters} onSubmit={applyRange}>
                    <TextField label="Start" type="datetime-local" step={60} value={start} onChange={(event) => setStart(event.target.value)} />
                    <TextField label="Stop" type="datetime-local" step={60} value={stop} onChange={(event) => setStop(event.target.value)} error={rangeError ?? undefined} />
                    <Button type="submit" size="sm" variant="secondary" disabled={rangeError !== null || refreshing}>
                      Apply
                    </Button>
                    <TextField label="Find" placeholder="Process, file, context…" value={find} onChange={(event) => setFind(event.target.value)} />
                  </form>
                  {selinux.auditProblem ? <Alert variant="error">{selinux.auditProblem}</Alert> : null}
                  {selinux.denialsTruncated ? <p className={styles.hint}>Showing the newest 1,000 records. Choose a shorter time range to see the rest.</p> : null}
                  {denials.length === 0 && !selinux.auditProblem ? <p className={styles.vmEmpty}>Nothing was denied in this time range.</p> : null}
                  {denials.length > 0 ? (
                    <table className={styles.vmTable}>
                      <thead>
                        <tr>
                          <th scope="col">
                            <span className="visually-hidden">Select</span>
                          </th>
                          <th scope="col">Result</th>
                          <th scope="col">Time</th>
                          <th scope="col">Process</th>
                          <th scope="col">Denied</th>
                          <th scope="col">Target</th>
                        </tr>
                      </thead>
                      <tbody>
                        {denials.map((denial, index) => {
                          const key = `${denial.eventId}-${index}`
                          const open = expanded === index
                          return (
                            <Fragment key={key}>
                              <tr>
                                <td>
                                  {denial.permissions.length > 0 ? (
                                    <input
                                      type="checkbox"
                                      aria-label={`Select ${denial.permissions.join(' ')} by ${denial.process ?? 'unknown'}`}
                                      checked={selected.has(denial.eventId)}
                                      onChange={() => toggle(denial.eventId)}
                                    />
                                  ) : null}
                                </td>
                                <td className={styles.logNarrow}>
                                  <span className={`${styles.dot} ${denial.blocked ? styles.failed : styles.connecting}`} /> {denial.permissions.length === 0 ? 'Error' : denial.blocked ? 'Blocked' : 'Logged'}
                                </td>
                                <td className={styles.logNarrow}>{timeFormat.format(new Date(denial.time))}</td>
                                <td className={styles.logNarrow}>{denial.process ?? '—'}</td>
                                <td className={styles.logNarrow}>
                                  {denial.permissions.length > 0 ? `${denial.permissions.join(' ')}${denial.targetClass ? ` (${denial.targetClass})` : ''}` : denial.type}
                                </td>
                                <td className={styles.logMessage}>
                                  <button type="button" className={styles.logToggle} aria-expanded={open} onClick={() => setExpanded(open ? null : index)}>
                                    {denial.target ?? denial.targetContext ?? denial.raw}
                                  </button>
                                </td>
                              </tr>
                              {open ? (
                                <tr>
                                  <td colSpan={6} className={styles.logDetails}>
                                    <dl>
                                      {[
                                        ['Source', denial.sourceContext],
                                        ['Target', denial.targetContext],
                                        ['PID', denial.pid],
                                        ['Audit event', String(denial.eventId)],
                                      ].map(([label, value]) =>
                                        value ? (
                                          <div key={label}>
                                            <dt>{label}</dt>
                                            <dd>{value}</dd>
                                          </div>
                                        ) : null,
                                      )}
                                    </dl>
                                    <pre className={styles.logRaw}>{denial.raw}</pre>
                                  </td>
                                </tr>
                              ) : null}
                            </Fragment>
                          )
                        })}
                      </tbody>
                    </table>
                  ) : null}
                </section>

                <section className={styles.storageSection} aria-label="Policies">
                  <header className={styles.storageHeader}>
                    <h2>Policies</h2>
                    <span className={styles.hint}>{distribution.length} from the distribution</span>
                    <Button size="sm" variant="secondary" onClick={openNew} disabled={busy}>
                      Add policy
                    </Button>
                  </header>
                  {local.length === 0 ? (
                    <p className={styles.vmEmpty}>No policies have been added to this host.</p>
                  ) : (
                    <table className={styles.vmTable}>
                      <thead>
                        <tr>
                          <th scope="col">Module</th>
                          <th scope="col">Priority</th>
                          <th scope="col">
                            <span className="visually-hidden">Actions</span>
                          </th>
                        </tr>
                      </thead>
                      <tbody>
                        {local.map((module) => (
                          <tr key={`${module.name} ${module.priority}`}>
                            <td className={styles.vmName}>{module.name}</td>
                            <td>{module.priority}</td>
                            <td className={styles.vmActions}>
                              <Button size="sm" variant="secondary" onClick={() => setRemoving(module)} disabled={busy}>
                                Remove
                              </Button>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  )}
                  <p>
                    <Button size="sm" variant="ghost" onClick={() => setShowDistribution((s) => !s)} aria-expanded={showDistribution}>
                      {showDistribution ? 'Hide the distribution’s modules' : 'Show the distribution’s modules'}
                    </Button>
                  </p>
                  {showDistribution ? <p className={styles.hint}>{distribution.map((m) => m.name).join(', ') || 'None'}</p> : null}
                </section>
              </>
            )}
          </div>
        ) : null}
      </div>

      {editing?.step === 'name' ? (
        <Dialog
          title="Create a policy from these denials"
          description={`On ${session.title}. Nothing is installed yet.`}
          onClose={() => setEditing(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setEditing(null)}>
                Cancel
              </Button>
              <Button type="submit" form="selinux-name" loading={busy} disabled={!MODULE_NAME.test(editing.name.trim())}>
                Generate
              </Button>
            </>
          }
        >
          <form id="selinux-name" onSubmit={generate}>
            <TextField
              label="Policy name"
              value={editing.name}
              onChange={(event) => setEditing({ ...editing, name: event.target.value })}
              error={MODULE_NAME.test(editing.name.trim()) ? undefined : 'Use up to 64 letters, digits or underscores, starting with a letter.'}
              spellCheck={false}
              autoComplete="off"
              data-autofocus
            />
          </form>
          {dialogProblem ? <Alert variant="error">{dialogProblem}</Alert> : null}
          <p>audit2allow on the host writes rules that allow what these {editing.denials.length} record{editing.denials.length === 1 ? '' : 's'} denied. You’ll see them before anything is installed.</p>
        </Dialog>
      ) : null}

      {editing?.step === 'source' ? (
        <Dialog
          title={newName ? `Install ${newName}?` : 'Install a policy?'}
          description={`On ${session.title}.`}
          onClose={() => setEditing(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setEditing(null)}>
                Cancel
              </Button>
              <Button type="submit" form="selinux-source" variant="danger" loading={busy} disabled={newName === null || clashes}>
                Install
              </Button>
            </>
          }
        >
          <form id="selinux-source" onSubmit={install}>
            <TextArea
              label="Policy source (.te)"
              className={styles.logSource}
              rows={14}
              value={editing.source}
              onChange={(event) => setEditing({ step: 'source', source: event.target.value })}
              spellCheck={false}
              error={newName === null ? 'It must start with its name and version, e.g. module my_policy 1.0;' : clashes ? `The distribution has a module called ${newName}. Choose another name.` : undefined}
            />
          </form>
          {dialogProblem ? <Alert variant="error">{dialogProblem}</Alert> : null}
          <p>
            Anything these rules allow, SELinux stops blocking, across the whole host. Read them first: a comment from audit2allow may name a boolean that does the job with less.
            {replaces ? ` This replaces the ${newName} already installed.` : ''}
          </p>
        </Dialog>
      ) : null}

      {removing ? (
        <Dialog
          title={`Remove ${removing.name}?`}
          description={`On ${session.title}.`}
          onClose={() => setRemoving(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setRemoving(null)}>
                Cancel
              </Button>
              <Button variant="danger" onClick={remove}>
                Remove
              </Button>
            </>
          }
        >
          <p>What this policy allows is blocked again as soon as it’s gone, which can stop services that rely on it. Its source isn’t kept, so note it first if you may want it back.</p>
        </Dialog>
      ) : null}
    </div>
  )
}
