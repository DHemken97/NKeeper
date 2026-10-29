import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { TextField } from '../../../components/ui/TextField'
import { api, ApiError } from '../../../lib/api'
import { hostKeyPrompt, kvmRequestBody } from '../kvm'
import {
  CONFIRM,
  iscsiRows,
  lastPathToMounted,
  multipathTargets,
  sessionMaps,
  suggestedStorageId,
  type DrbdResource,
  type LvmMap,
  type StorageActionRequest,
  type StorageResponse,
  type StorageStatus,
} from '../storage'
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

/** What's on a map, for Proxmox LVM: its storage, its volume group, something else, or nothing. */
function lvmState(lvm: LvmMap, node: string): string {
  if (lvm.mounted) {
    return `Storage ${lvm.storage} (VG ${lvm.volumeGroup})`
  }
  if (lvm.storage) {
    return `VG ${lvm.volumeGroup} · storage ${lvm.storage}, not on ${node}`
  }
  if (lvm.volumeGroup) {
    return `VG ${lvm.volumeGroup} · no Proxmox storage`
  }
  return lvm.signature ? `Has ${lvm.signature}` : 'Blank'
}

/**
 * A Linux host's shared storage over SSH: iSCSI targets and sessions, multipath maps and DRBD
 * resources, with their everyday actions. On a Proxmox node, multipath maps can be mounted as LVM
 * storage (and a target logged in to and mounted in one go). Actions that take storage away ask first.
 */
export function StorageView({ session, active, onShowHost }: StorageViewProps) {
  const [phase, setPhase] = useState<SessionPhase>({ kind: 'connecting' })
  const [storage, setStorage] = useState<StorageStatus | null>(null)
  const [refreshing, setRefreshing] = useState(true)
  const [attempt, setAttempt] = useState(0)
  const [busy, setBusy] = useState<StorageActionRequest | null>(null)
  const [confirming, setConfirming] = useState<StorageActionRequest | null>(null)
  /** A map being mounted as new Proxmox storage, and the storage ID entered. */
  const [mounting, setMounting] = useState<{ lvm: LvmMap; storage: string } | null>(null)
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

  /**
   * Runs an action (asking first for those in CONFIRM), then shows the host's storage afterwards.
   * Resolves to that storage once the action has finished, or null when it didn't run or failed.
   */
  function act(request: StorageActionRequest, confirmed = false): Promise<StorageStatus | null> {
    if (!confirmed && CONFIRM[request.action]) {
      setConfirming(request)
      return Promise.resolve(null)
    }
    setConfirming(null)
    setMounting(null)
    setBusy(request)
    setResult(null)
    return api
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
          return response.storage
        },
        (error: unknown) => {
          setResult({ ok: false, message: error instanceof ApiError ? error.message : `Couldn’t change ${subject(request)}.` })
          return null
        },
      )
      .finally(() => setBusy(null))
  }

  /** Mounts a map: straight away onto the storage its volume group already has, else after naming a new storage. */
  function mount(lvm: LvmMap, current: StorageStatus) {
    if (lvm.storage) {
      void act({ action: 'lvm.mount', name: lvm.map })
      return
    }
    const map = current.multipath?.find((m) => m.name === lvm.map)
    setMounting({ lvm, storage: suggestedStorageId(lvm, map, current.iscsi) })
  }

  /** Logs in to a target, then mounts the multipath map its disk shows up in. */
  async function logInAndMount(target: string, portal: string) {
    const after = await act({ action: 'iscsi.login', target, portal })
    const session = after?.iscsi?.sessions.find((s) => s.target === target && s.portal === portal)
    if (!after || !session) {
      return
    }
    const maps = sessionMaps(session, after.multipath)
    const lvm = maps.map((m) => after.proxmox?.maps.find((l) => l.map === m.name)).find((l) => l && !l.mounted && (l.volumeGroup || !l.signature))
    if (lvm) {
      mount(lvm, after)
    } else if (maps.length === 0) {
      setResult((r) => ({
        ok: true,
        message: `${r?.message ?? ''} There’s no multipath map for it to mount yet: mount it under Multipath once it shows up (Reload maps).`,
      }))
    }
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
                        const mountedHere = row.session ? lastPathToMounted(row.session, storage) : null
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
                            <td className={`${styles.vmActions} ${styles.storageActions}`}>
                              {row.session ? (
                                <>
                                  <Button
                                    size="sm"
                                    variant="secondary"
                                    onClick={() => act({ action: 'iscsi.logout', ...request })}
                                    disabled={disabled || mountedHere !== null}
                                  >
                                    Log out
                                  </Button>
                                  {mountedHere ? <div className={styles.hint}>{mountedHere.name} is mounted: unmount it first</div> : null}
                                </>
                              ) : (
                                <>
                                  <Button
                                    size="sm"
                                    variant="secondary"
                                    onClick={() => act({ action: 'iscsi.login', ...request })}
                                    disabled={disabled}
                                    loading={busy?.action === 'iscsi.login' && busy.target === row.target && busy.portal === row.portal}
                                  >
                                    Log in
                                  </Button>
                                  {storage.proxmox && storage.multipath ? (
                                    <Button size="sm" variant="secondary" onClick={() => void logInAndMount(row.target, row.portal)} disabled={disabled}>
                                      Log in &amp; mount LVM
                                    </Button>
                                  ) : null}
                                </>
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
                        <th scope="col">iSCSI target</th>
                        <th scope="col">Size</th>
                        <th scope="col">Paths</th>
                        {storage.proxmox ? <th scope="col">Proxmox LVM</th> : null}
                        <th scope="col">
                          <span className="visually-hidden">Actions</span>
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {storage.multipath.map((map) => {
                        const healthy = map.paths.filter((p) => p.dmState === 'active' && p.pathState === 'ready').length
                        const targets = multipathTargets(map, storage.iscsi)
                        const lvm = storage.proxmox?.maps.find((l) => l.map === map.name)
                        const isBusy = (action: StorageActionRequest['action']) => busy?.action === action && busy.name === map.name
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
                            <td>
                              {targets.length === 0
                                ? '—'
                                : targets.map((t) => (
                                    <div key={t.target}>
                                      {t.target}
                                      <div className={styles.hint}>via {t.portals.join(', ')}</div>
                                    </div>
                                  ))}
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
                            {storage.proxmox ? (
                              <td>
                                {lvm ? (
                                  <>
                                    <span className={`${styles.dot} ${lvm.mounted ? styles.connected : ''}`} /> {lvmState(lvm, storage.proxmox.node)}
                                  </>
                                ) : (
                                  '—'
                                )}
                              </td>
                            ) : null}
                            <td className={`${styles.vmActions} ${styles.storageActions}`}>
                              {lvm?.mounted ? (
                                <Button size="sm" variant="secondary" onClick={() => act({ action: 'lvm.unmount', name: map.name })} disabled={disabled}>
                                  Unmount LVM
                                </Button>
                              ) : (
                                <>
                                  {lvm && (lvm.volumeGroup || !lvm.signature) ? (
                                    <Button size="sm" variant="secondary" onClick={() => mount(lvm, storage)} disabled={disabled} loading={isBusy('lvm.mount')}>
                                      Mount LVM
                                    </Button>
                                  ) : null}
                                  <Button size="sm" variant="secondary" onClick={() => act({ action: 'multipath.flush', name: map.name })} disabled={disabled}>
                                    Flush
                                  </Button>
                                </>
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

      {mounting ? (
        <Dialog
          title={`Mount ${mounting.lvm.map} as Proxmox LVM storage?`}
          description={`On ${session.title}${storage?.proxmox ? ` (node ${storage.proxmox.node})` : ''}.`}
          onClose={() => setMounting(null)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setMounting(null)}>
                Cancel
              </Button>
              <Button type="submit" form="lvm-mount" variant={mounting.lvm.volumeGroup ? 'primary' : 'danger'} disabled={!mounting.storage.trim()}>
                {mounting.lvm.volumeGroup ? 'Mount' : 'Initialize and mount'}
              </Button>
            </>
          }
        >
          <form
            id="lvm-mount"
            onSubmit={(event) => {
              event.preventDefault()
              void act({ action: 'lvm.mount', name: mounting.lvm.map, storage: mounting.storage.trim() })
            }}
          >
            <TextField
              label="Storage ID"
              value={mounting.storage}
              onChange={(event) => setMounting({ ...mounting, storage: event.target.value })}
              hint="The name Proxmox shows it by, e.g. san-vms."
              autoFocus
            />
          </form>
          <p>
            {mounting.lvm.volumeGroup
              ? `Adds an LVM storage on volume group ${mounting.lvm.volumeGroup} for this node, for disk images and containers, marked shared.`
              : `${mounting.lvm.map} looks blank. NKeeper makes it an LVM physical volume and a volume group named like the storage (pvcreate, vgcreate), then adds the storage for this node, for disk images and containers, marked shared. NKeeper checks it’s blank again first and refuses if it isn’t.`}{' '}
            Other nodes logged in to it can mount it too.
          </p>
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
