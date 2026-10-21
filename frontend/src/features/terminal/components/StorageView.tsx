import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { TextField } from '../../../components/ui/TextField'
import { api, ApiError } from '../../../lib/api'
import { hostKeyPrompt, kvmRequestBody } from '../kvm'
import { CONFIRM, iscsiRows, type DrbdResource, type StorageActionRequest, type StorageResponse, type StorageStatus } from '../storage'
import type { RemoteSession, SessionPhase } from '../types'
import { EndedCard, OpeningCard, SessionStatusBar, TrustCard } from './SessionChrome'
import styles from './Session.module.css'

interface StorageViewProps {
  session: RemoteSession
  active: boolean
  onShowHost: (hostId: string) => void
}

/** What an action acts on, for the confirmation and messages. */
function subject(request: StorageActionRequest): string {
  return request.target ? `${request.target} (${request.portal})` : (request.name ?? request.portal ?? 'this host')
}

/**
 * A Linux host's shared storage over SSH: iSCSI targets and sessions, multipath maps and DRBD
 * resources, with their everyday actions. Actions that take storage away ask first.
 */
export function StorageView({ session, active, onShowHost }: StorageViewProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [storage, setStorage] = useState<StorageStatus | null>(null)
  const [refreshing, setRefreshing] = useState(true)
  const [attempt, setAttempt] = useState(0)
  const [busy, setBusy] = useState<StorageActionRequest | null>(null)
  const [confirming, setConfirming] = useState<StorageActionRequest | null>(null)
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null)
  const [portal, setPortal] = useState('')
  const trustRef = useRef<string | undefined>(undefined)
  const target = session.title

  useEffect(() => {
    let cancelled = false
    api
      .post<StorageResponse>('/api/storage/status', { body: kvmRequestBody(session.request, trustRef.current) })
      .then(
        (response) => {
          if (cancelled) {
            return
          }
          if (response.status === 'trustRequired' && response.hostKey) {
            setPhase({ kind: 'hostKey', prompt: hostKeyPrompt(response.hostKey), target })
            return
          }
          setStorage(response.storage)
          setPhase({ kind: 'connected', target })
        },
        (error: unknown) => {
          if (!cancelled) {
            setPhase({
              kind: 'ended',
              target,
              code: error instanceof ApiError ? error.code : undefined,
              message: error instanceof ApiError ? error.message : 'Couldn’t read this host’s storage.',
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
  }, [session.request, target, attempt])

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

  /** Runs an action (asking first for those in CONFIRM), then shows the host's storage afterwards. */
  function act(request: StorageActionRequest, confirmed = false) {
    if (!confirmed && CONFIRM[request.action]) {
      setConfirming(request)
      return
    }
    setConfirming(null)
    setBusy(request)
    setResult(null)
    api
      .post<StorageResponse>('/api/storage/action', { body: { ...kvmRequestBody(session.request, trustRef.current), ...request } })
      .then(
        (response) => {
          if (response.storage) {
            setStorage(response.storage)
          }
          if (request.action === 'iscsi.discover') {
            setPortal('')
          }
          setResult({ ok: true, message: response.message ?? 'Done.' })
        },
        (error: unknown) => setResult({ ok: false, message: error instanceof ApiError ? error.message : `Couldn’t change ${subject(request)}.` }),
      )
      .finally(() => setBusy(null))
  }

  function discover(event: FormEvent) {
    event.preventDefault()
    if (portal.trim()) {
      act({ action: 'iscsi.discover', portal: portal.trim() })
    }
  }

  const disabled = busy !== null
  const nothing = storage && !storage.iscsi && !storage.multipath && !storage.drbd
  const confirmInfo = confirming ? CONFIRM[confirming.action] : undefined

  return (
    <div className={styles.session} hidden={!active} role="tabpanel" aria-label={`${session.title} storage`}>
      <SessionStatusBar phase={phase}>
        {phase.kind === 'connected' ? (
          <Button variant="ghost" size="sm" onClick={refresh} disabled={refreshing || disabled}>
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
        {phase.kind === 'connected' && storage ? (
          <div className={styles.vmList}>
            {result ? <Alert variant={result.ok ? 'info' : 'error'}>{result.message}</Alert> : null}
            {nothing ? <p className={styles.vmEmpty}>This host has none of iSCSI (iscsiadm), multipath or DRBD (drbdadm) installed.</p> : null}

            {storage.iscsi ? (
              <section className={styles.storageSection} aria-label="iSCSI">
                <header className={styles.storageHeader}>
                  <h2>iSCSI</h2>
                  {storage.iscsi.initiatorName ? <span className={styles.hint}>Initiator {storage.iscsi.initiatorName}</span> : null}
                  <Button
                    size="sm"
                    variant="secondary"
                    onClick={() => act({ action: 'iscsi.rescan' })}
                    disabled={disabled || storage.iscsi.sessions.length === 0}
                    loading={busy?.action === 'iscsi.rescan'}
                  >
                    Rescan sessions
                  </Button>
                </header>
                <form className={styles.storageForm} onSubmit={discover}>
                  <TextField
                    label="Discover targets at portal"
                    placeholder="10.0.0.5 or 10.0.0.5:3260"
                    value={portal}
                    onChange={(event) => setPortal(event.target.value)}
                    disabled={disabled}
                  />
                  <Button type="submit" size="sm" variant="secondary" disabled={disabled || !portal.trim()} loading={busy?.action === 'iscsi.discover'}>
                    Discover
                  </Button>
                </form>
                {iscsiRows(storage.iscsi).length === 0 ? (
                  <p className={styles.vmEmpty}>No targets yet. Discover them at the storage’s portal.</p>
                ) : (
                  <table className={styles.vmTable}>
                    <thead>
                      <tr>
                        <th scope="col">Target</th>
                        <th scope="col">Portal</th>
                        <th scope="col">State</th>
                        <th scope="col">Disks</th>
                        <th scope="col">
                          <span className="visually-hidden">Actions</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {iscsiRows(storage.iscsi).map((row) => {
                        const request = { target: row.target, portal: row.portal }
                        const loggedIn = row.session?.state === 'LOGGED_IN'
                        return (
                          <tr key={`${row.target} ${row.portal}`}>
                            <td className={styles.vmName}>{row.target}</td>
                            <td>
                              {row.portal}
                              {row.session?.connectedTo?.length ? <div className={styles.hint}>connected via {row.session.connectedTo.join(', ')}</div> : null}
                            </td>
                            <td>
                              <span className={`${styles.dot} ${loggedIn ? styles.connected : row.session ? styles.failed : ''}`} />{' '}
                              {row.session ? row.session.state.replace('_', ' ').toLowerCase() : 'not logged in'}
                            </td>
                            <td>{row.session?.disks.join(', ') || '—'}</td>
                            <td className={styles.vmActions}>
                              {row.session ? (
                                <Button size="sm" variant="secondary" onClick={() => act({ action: 'iscsi.logout', ...request })} disabled={disabled}>
                                  Log out
                                </Button>
                              ) : (
                                <Button
                                  size="sm"
                                  variant="secondary"
                                  onClick={() => act({ action: 'iscsi.login', ...request })}
                                  disabled={disabled}
                                  loading={busy?.action === 'iscsi.login' && busy.target === row.target && busy.portal === row.portal}
                                >
                                  Log in
                                </Button>
                              )}
                            </td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                )}
              </section>
            ) : null}

            {storage.multipath ? (
              <section className={styles.storageSection} aria-label="Multipath">
                <header className={styles.storageHeader}>
                  <h2>Multipath</h2>
                  <Button size="sm" variant="secondary" onClick={() => act({ action: 'multipath.reload' })} disabled={disabled} loading={busy?.action === 'multipath.reload'}>
                    Reload maps
                  </Button>
                </header>
                {storage.multipath.length === 0 ? (
                  <p className={styles.vmEmpty}>No multipath maps.</p>
                ) : (
                  <table className={styles.vmTable}>
                    <thead>
                      <tr>
                        <th scope="col">Map</th>
                        <th scope="col">Device</th>
                        <th scope="col">Size</th>
                        <th scope="col">Paths</th>
                        <th scope="col">
                          <span className="visually-hidden">Actions</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {storage.multipath.map((map) => {
                        const healthy = map.paths.filter((p) => p.dmState === 'active' && p.pathState === 'ready').length
                        return (
                          <tr key={map.name}>
                            <td className={styles.vmName}>
                              {map.name}
                              {map.wwid !== map.name ? <div className={styles.hint}>{map.wwid}</div> : null}
                            </td>
                            <td>
                              {map.dmDevice}
                              {map.product ? <div className={styles.hint}>{map.product}</div> : null}
                            </td>
                            <td>{map.size ?? '—'}</td>
                            <td>
                              <span className={`${styles.dot} ${healthy === map.paths.length && healthy > 0 ? styles.connected : styles.failed}`} /> {healthy} of{' '}
                              {map.paths.length} ready
                              {map.paths.map((p) => (
                                <div key={p.address} className={styles.hint}>
                                  {p.device} ({p.address}) {p.dmState} · {p.pathState} · {p.onlineState}
                                </div>
                              ))}
                            </td>
                            <td className={styles.vmActions}>
                              <Button size="sm" variant="secondary" onClick={() => act({ action: 'multipath.flush', name: map.name })} disabled={disabled}>
                                Flush
                              </Button>
                            </td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                )}
              </section>
            ) : null}

            {storage.drbd ? (
              <section className={styles.storageSection} aria-label="DRBD">
                <header className={styles.storageHeader}>
                  <h2>DRBD</h2>
                </header>
                {storage.drbd.length === 0 ? (
                  <p className={styles.vmEmpty}>No DRBD resources are configured on this host.</p>
                ) : (
                  <table className={styles.vmTable}>
                    <thead>
                      <tr>
                        <th scope="col">Resource</th>
                        <th scope="col">Role</th>
                        <th scope="col">Disk</th>
                        <th scope="col">Peers</th>
                        <th scope="col">
                          <span className="visually-hidden">Actions</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {storage.drbd.map((resource) => (
                        <DrbdRow key={resource.name} resource={resource} busy={busy} disabled={disabled} onAct={act} />
                      ))}
                    </tbody>
                  </table>
                )}
              </section>
            ) : null}
          </div>
        ) : null}
      </div>

      {confirming && confirmInfo ? (
        <Dialog
          title={`${confirmInfo.label} ${subject(confirming)}?`}
          description={`On ${session.title}.`}
          onClose={() => setConfirming(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setConfirming(null)}>
                Cancel
              </Button>
              <Button variant="danger" onClick={() => act(confirming, true)}>
                {confirmInfo.label}
              </Button>
            </>
          }
        >
          <p>{confirmInfo.explanation}</p>
        </Dialog>
      ) : null}
    </div>
  )
}

function DrbdRow({
  resource,
  busy,
  disabled,
  onAct,
}: {
  resource: DrbdResource
  busy: StorageActionRequest | null
  disabled: boolean
  onAct: (request: StorageActionRequest) => void
}) {
  const button = (action: StorageActionRequest['action'], label: string) => (
    <Button
      size="sm"
      variant="secondary"
      onClick={() => onAct({ action, name: resource.name })}
      disabled={disabled}
      loading={busy?.action === action && busy.name === resource.name}
    >
      {label}
    </Button>
  )
  const standAlone = resource.peers.length > 0 && resource.peers.every((p) => p.connectionState === 'StandAlone')
  const healthy =
    resource.up &&
    resource.volumes.every((v) => v.diskState === 'UpToDate') &&
    resource.peers.every((p) => p.connectionState === 'Connected' && p.volumes.every((v) => v.diskState === 'UpToDate'))

  return (
    <tr>
      <td className={styles.vmName}>
        <span className={`${styles.dot} ${healthy ? styles.connected : resource.up ? styles.connecting : ''}`} /> {resource.name}
      </td>
      <td>{resource.up ? (resource.role ?? 'Unknown') : 'Down'}</td>
      <td>
        {resource.volumes.map((v) => (
          <div key={v.volume}>
            {resource.volumes.length > 1 ? `${v.volume}: ` : ''}
            {v.diskState ?? '—'}
            {v.minor !== null ? <span className={styles.hint}> · /dev/drbd{v.minor}</span> : null}
          </div>
        ))}
      </td>
      <td>
        {resource.peers.map((peer) => (
          <div key={peer.name}>
            {peer.name}: {peer.connectionState}
            {peer.role ? ` · ${peer.role}` : ''}
            {peer.volumes.map((v) => (
              <div key={v.volume} className={styles.hint}>
                {[v.replicationState, v.diskState, v.percentInSync !== null && v.percentInSync < 100 ? `${v.percentInSync}% in sync` : null].filter(Boolean).join(' · ')}
              </div>
            ))}
          </div>
        ))}
      </td>
      <td className={`${styles.vmActions} ${styles.storageActions}`}>
        {resource.up ? (
          <>
            {resource.role === 'Primary' ? button('drbd.secondary', 'Secondary') : button('drbd.primary', 'Primary')}
            {standAlone ? button('drbd.connect', 'Connect') : button('drbd.disconnect', 'Disconnect')}
            {button('drbd.adjust', 'Adjust')}
            {button('drbd.down', 'Down')}
          </>
        ) : (
          button('drbd.up', 'Up')
        )}
      </td>
    </tr>
  )
}
