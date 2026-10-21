import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Dialog } from '../../../components/ui/Dialog'
import { Spinner } from '../../../components/ui/Spinner'
import { api, ApiError } from '../../../lib/api'
import { guestId, type GuestVia } from '../hostMenu'
import type { Host, HostCredential } from '../types'
import fieldStyles from './ImportHostsDialog.module.css'
import listStyles from './ListEditor.module.css'
import styles from './MigrateDialog.module.css'
import scanStyles from './ScanDialog.module.css'

interface MigrateDialogProps {
  /** The VM or container. */
  vm: Host
  /** The hypervisor it's on, whose service and login reach it (Proxmox API or KVM over SSH). */
  node: Host
  serviceIndex: number
  via: GuestVia
  credentialId?: string
  oneOff?: { username: string; password: string; domain?: string }
  /** Every host the user can see, to move to and to log in to for storage. */
  hosts: Host[]
  /** After a successful move: the NKeeper host it now runs on, when there is one. */
  onMoved: (vm: Host, hypervisor: Host | undefined) => void
  onClose: () => void
}

interface StorageLayer {
  kind: 'lvm' | 'drbd' | 'multipath' | 'iscsi' | 'local'
  name: string
  detail: string | null
}

interface GuestDisk {
  name: string
  path: string
  layout: 'managed' | 'network' | 'shared' | 'filesystem' | 'block' | 'local'
  mount: { device: string; mountPoint: string; fsType: string } | null
  layers: StorageLayer[]
  storage: string | null
  note: string | null
}

interface PlannedStep {
  kind: string
  side: 'source' | 'target' | 'both' | 'hypervisor'
  title: string
  critical: boolean
}

interface Plan {
  mode: 'live' | 'offline'
  guestState: string
  sourceName: string
  targetName: string
  disks: GuestDisk[]
  steps: PlannedStep[]
  blockers: string[]
  notes: string[]
  choices: {
    liveAvailable: boolean
    liveUnavailableReason: string | null
    logoutOldIscsi: boolean
    iscsiToLogOut: string[]
    iscsiKept: string[]
    hasIscsi: boolean
    hasDrbd: boolean
    hasLocalDisks: boolean
  }
}

interface MigrationResponse {
  status: 'ready' | 'started' | 'trustRequired'
  login: Role | null
  hostKey: { algorithm: string; fingerprint: string } | null
  certificate: { fingerprint: string; subject: string } | null
  plan: Plan | null
  jobId: string | null
}

interface JobView {
  state: 'running' | 'succeeded' | 'rolledBack' | 'rollbackFailed'
  mode: string
  steps: { title: string; side: string; state: string; message: string | null; critical: boolean }[]
  message: string | null
}

/** Which login a trust prompt is about. */
type Role = 'source' | 'target' | 'sourceShell'

/** A host and the service and credential to log in to it with. */
interface Login {
  host: Host
  serviceIndex: number
  credentialId: string
}

interface Options {
  live: boolean
  startAfter: boolean
  /** null: the default for the hypervisor (log out for KVM, stay for Proxmox). */
  logoutOldIscsi: boolean | null
  drbdDownOnOld: boolean
  withLocalDisks: boolean
}

type Phase =
  | { kind: 'setup' }
  | { kind: 'checking' }
  | { kind: 'trust'; role: Role; fingerprint: string; shown: string }
  | { kind: 'plan'; plan: Plan }
  | { kind: 'running'; plan: Plan; jobId: string; job: JobView | null }
  | { kind: 'failed'; message: string }

const POLL_MS = 1500

const LAYOUTS: Record<GuestDisk['layout'], string> = {
  managed: 'Proxmox storage',
  network: 'Network storage',
  shared: 'Cluster file system',
  filesystem: 'File system on shared storage',
  block: 'Shared block device',
  local: 'Local disk',
}

const STEP_STATES: Record<string, string> = {
  pending: '·',
  running: '…',
  done: '✓',
  skipped: '–',
  warning: '!',
  failed: '✗',
  undoing: '↺',
  undone: '↺',
  undoFailed: '✗',
}

/** Logins that can be used without asking: a username and a stored password or key. */
function usable(host: Host): HostCredential[] {
  return host.credentials.filter((c) => c.username && (c.hasPassword || c.hasPrivateKey))
}

/** The host's SSH service, or its KVM service (SSH too). */
function shellService(host: Host): number {
  const ssh = host.services.findIndex((s) => s.protocol === 'Ssh')
  return ssh !== -1 ? ssh : host.services.findIndex((s) => s.protocol === 'Kvm')
}

/** The NKeeper host for a Proxmox node, by name or short host name, that can be logged in to over SSH. */
function hostForNode(hosts: Host[], node: string): Host | undefined {
  const name = node.toLowerCase()
  return hosts.find(
    (h) => (h.name.toLowerCase() === name || h.hostname?.split('.')[0].toLowerCase() === name) && shellService(h) !== -1 && usable(h).length > 0,
  )
}

function loginFor(host: Host | undefined, serviceIndex?: number): Login | null {
  const credential = host ? usable(host)[0] : undefined
  return host && credential ? { host, serviceIndex: serviceIndex ?? shellService(host), credentialId: credential.id } : null
}

/** A disk's storage, top down, e.g. "vg0/web → mpatha → iSCSI disk1 @ 10.0.0.5:3260". */
function chain(disk: GuestDisk): string {
  const layers = disk.layers.map((l) =>
    l.kind === 'iscsi' ? `iSCSI ${l.name}${l.detail ? ` @ ${l.detail}` : ''}` : l.kind === 'multipath' ? `multipath ${l.detail ?? l.name}` : l.kind === 'drbd' ? `DRBD ${l.name}` : l.kind === 'lvm' ? `LV ${l.name}` : l.name,
  )
  return [disk.mount ? `${disk.mount.mountPoint} (${disk.mount.fsType})` : null, ...layers].filter(Boolean).join(' → ')
}

/**
 * Moves a VM or container to another host with its storage: choose where and how, check (NKeeper
 * reads both hosts and shows the disks, what blocks the move and every step), then follow the
 * migration, which runs on the server and is put back if a step fails.
 */
export function MigrateDialog({ vm, node, serviceIndex, via, credentialId, oneOff, hosts, onMoved, onClose }: MigrateDialogProps) {
  const proxmox = via === 'proxmox'
  const [phase, setPhase] = useState<Phase>({ kind: proxmox ? 'checking' : 'setup' })
  const [nodes, setNodes] = useState<{ name: string; online: boolean }[]>([])
  const [guestNode, setGuestNode] = useState<string | null>(null)
  const [targetNode, setTargetNode] = useState('')
  const [targetHostId, setTargetHostId] = useState('')
  const [targetCredentialId, setTargetCredentialId] = useState('')
  const [sourceShellCredentialId, setSourceShellCredentialId] = useState('')
  const [options, setOptions] = useState<Options>({ live: true, startAfter: true, logoutOldIscsi: null, drbdDownOnOld: false, withLocalDisks: false })
  const [loadAttempt, setLoadAttempt] = useState(0)
  const trust = useRef<Partial<Record<Role, string>>>({})
  const moved = useRef(false)
  const vmId = guestId(vm)?.id ?? ''

  // KVM: other KVM hosts; Proxmox: the cluster's other online nodes.
  const kvmTargets = useMemo(
    () => hosts.filter((h) => h.id !== node.id && h.services.some((s) => s.protocol === 'Kvm') && !h.services.some((s) => s.protocol === 'Proxmox')),
    [hosts, node.id],
  )
  const targetHost = proxmox ? (targetNode ? hostForNode(hosts, targetNode) : undefined) : kvmTargets.find((h) => h.id === targetHostId)
  const sourceShellHost = proxmox && guestNode ? hostForNode(hosts, guestNode) : undefined

  const sourceLogin = { hostId: node.id, serviceIndex, credentialId, username: oneOff?.username, password: oneOff?.password }
  const targetLogin = (() => {
    const login = loginFor(targetHost, proxmox ? undefined : targetHost?.services.findIndex((s) => s.protocol === 'Kvm'))
    return login ? { hostId: login.host.id, serviceIndex: login.serviceIndex, credentialId: targetCredentialId || login.credentialId } : null
  })()
  const sourceShellLogin = (() => {
    const login = loginFor(sourceShellHost)
    return login ? { hostId: login.host.id, serviceIndex: login.serviceIndex, credentialId: sourceShellCredentialId || login.credentialId } : null
  })()

  const body = () => ({
    via,
    vmHostId: vm.id,
    vm: vmId,
    source: { ...sourceLogin, trustFingerprint: trust.current.source },
    targetNode: proxmox ? targetNode : undefined,
    target: targetLogin ? { ...targetLogin, trustFingerprint: trust.current.target } : undefined,
    sourceShell: proxmox && targetLogin && sourceShellLogin ? { ...sourceShellLogin, trustFingerprint: trust.current.sourceShell } : undefined,
    options: { ...options, logoutOldIscsi: options.logoutOldIscsi ?? undefined },
  })

  // Proxmox: where the guest is now, and the nodes it can move to.
  useEffect(() => {
    if (!proxmox) {
      return
    }
    let cancelled = false
    const load = () =>
      api
        .post<{ status: string; guest: { node: string } | null; nodes: { name: string; online: boolean }[] | null; certificate: { fingerprint: string; subject: string } | null }>(
          '/api/proxmox/guest',
          { body: { ...sourceLogin, trustFingerprint: trust.current.source, vm: Number(vmId) } },
        )
        .then(
          (response) => {
            if (cancelled) {
              return
            }
            if (response.status === 'trustRequired' && response.certificate) {
              setPhase({ kind: 'trust', role: 'source', fingerprint: response.certificate.fingerprint, shown: `${response.certificate.fingerprint} (${response.certificate.subject})` })
              return
            }
            const here = response.guest?.node ?? null
            const list = response.nodes ?? []
            setGuestNode(here)
            setNodes(list)
            setTargetNode(list.find((n) => n.online && n.name !== here)?.name ?? '')
            setPhase({ kind: 'setup' })
          },
          (error: unknown) => !cancelled && setPhase({ kind: 'failed', message: error instanceof ApiError ? error.message : 'Couldn’t reach Proxmox.' }),
        )
    void load()
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- loads again once the certificate is trusted
  }, [loadAttempt])

  function check() {
    setPhase({ kind: 'checking' })
    api.post<MigrationResponse>('/api/migrations/plan', { body: body() }).then(
      (response) => {
        if (response.status === 'trustRequired' && response.login) {
          const fingerprint = response.hostKey?.fingerprint ?? response.certificate?.fingerprint ?? ''
          const shown = response.hostKey ? `${response.hostKey.algorithm} ${response.hostKey.fingerprint}` : `${fingerprint} (${response.certificate?.subject ?? ''})`
          setPhase({ kind: 'trust', role: response.login, fingerprint, shown })
          return
        }
        setPhase(response.plan ? { kind: 'plan', plan: response.plan } : { kind: 'failed', message: 'NKeeper couldn’t plan the move.' })
      },
      (error: unknown) => setPhase({ kind: 'failed', message: error instanceof ApiError ? error.message : 'Couldn’t check the hosts.' }),
    )
  }

  function start(plan: Plan) {
    setPhase({ kind: 'running', plan, jobId: '', job: null })
    api.post<MigrationResponse>('/api/migrations', { body: body() }).then(
      (response) =>
        setPhase(
          response.jobId
            ? { kind: 'running', plan: response.plan ?? plan, jobId: response.jobId, job: null }
            : { kind: 'failed', message: 'NKeeper didn’t start the migration.' },
        ),
      (error: unknown) => setPhase({ kind: 'failed', message: error instanceof ApiError ? error.message : 'Couldn’t start the migration.' }),
    )
  }

  // Changing an option after checking checks again (the steps depend on it).
  const checked = phase.kind === 'plan'
  useEffect(() => {
    if (checked) {
      check()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- re-plan when the options change
  }, [options])

  // Follows the job until it ends.
  const jobId = phase.kind === 'running' ? phase.jobId : ''
  useEffect(() => {
    if (!jobId) {
      return
    }
    let cancelled = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const poll = () =>
      api.get<JobView>(`/api/migrations/${jobId}`).then(
        (job) => {
          if (cancelled) {
            return
          }
          setPhase((current) => (current.kind === 'running' ? { ...current, job } : current))
          if (job.state === 'running') {
            timer = setTimeout(poll, POLL_MS)
          } else if (job.state === 'succeeded' && !moved.current) {
            moved.current = true
            onMoved(vm, targetHost)
          }
        },
        () => !cancelled && (timer = setTimeout(poll, POLL_MS * 2)),
      )
    void poll()
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- one poll loop per job
  }, [jobId])

  const set = (change: Partial<Options>) => setOptions((current) => ({ ...current, ...change }))
  const plan = phase.kind === 'plan' || phase.kind === 'running' ? phase.plan : null
  const job = phase.kind === 'running' ? phase.job : null
  const finished = job !== null && job.state !== 'running'
  const canCheck = proxmox ? targetNode !== '' : targetLogin !== null
  const editable = phase.kind === 'setup' || phase.kind === 'plan'

  return (
    <Dialog
      title={`Migrate ${vm.name}`}
      description={proxmox ? `Through Proxmox, from ${guestNode ?? node.name}.` : `From ${node.name}, over SSH.`}
      size="lg"
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            {phase.kind === 'running' ? 'Close' : 'Cancel'}
          </Button>
          {phase.kind === 'trust' ? (
            <Button
              onClick={() => {
                trust.current = { ...trust.current, [phase.role]: phase.fingerprint }
                if (proxmox && phase.role === 'source' && guestNode === null) {
                  setPhase({ kind: 'checking' })
                  setLoadAttempt((n) => n + 1)
                  return
                }
                check()
              }}
            >
              Trust and continue
            </Button>
          ) : null}
          {phase.kind === 'setup' ? (
            <Button onClick={check} disabled={!canCheck}>
              Check
            </Button>
          ) : null}
          {phase.kind === 'plan' ? (
            <Button variant={phase.plan.mode === 'offline' && phase.plan.guestState === 'running' ? 'danger' : 'primary'} onClick={() => start(phase.plan)} disabled={phase.plan.blockers.length > 0}>
              {phase.plan.mode === 'live' ? 'Migrate live' : phase.plan.guestState === 'running' ? 'Shut down and migrate' : 'Migrate'}
            </Button>
          ) : null}
        </>
      }
    >
      <div className={fieldStyles.form}>
        {editable || phase.kind === 'checking' ? (
          <>
            <div className={fieldStyles.field}>
              <label className={fieldStyles.label} htmlFor="migrate-target">
                Move to
              </label>
              {proxmox ? (
                <select id="migrate-target" className={listStyles.input} value={targetNode} onChange={(event) => setTargetNode(event.target.value)} disabled={!editable}>
                  {nodes.every((n) => n.name === guestNode || !n.online) ? <option value="">No other node is online</option> : null}
                  {nodes
                    .filter((n) => n.name !== guestNode)
                    .map((n) => (
                      <option key={n.name} value={n.name} disabled={!n.online}>
                        {n.online ? n.name : `${n.name} (offline)`}
                      </option>
                    ))}
                </select>
              ) : (
                <select id="migrate-target" className={listStyles.input} value={targetHostId} onChange={(event) => setTargetHostId(event.target.value)} disabled={!editable}>
                  <option value="">Choose a KVM host…</option>
                  {kvmTargets.map((h) => (
                    <option key={h.id} value={h.id} disabled={usable(h).length === 0}>
                      {usable(h).length === 0 ? `${h.name} (no saved login)` : h.name}
                    </option>
                  ))}
                </select>
              )}
            </div>
            <LoginPicker
              id="migrate-target-login"
              label={proxmox ? `SSH login to ${targetNode || 'the new node'} (for storage)` : 'Login to the new host'}
              host={targetHost}
              value={targetLogin?.credentialId ?? ''}
              onChange={setTargetCredentialId}
              disabled={!editable}
              missing={proxmox && targetNode ? `No NKeeper host named ${targetNode} with an SSH login: NKeeper won’t check or move its storage.` : null}
            />
            {proxmox ? (
              <LoginPicker
                id="migrate-source-login"
                label={`SSH login to ${guestNode ?? 'the current node'} (for storage)`}
                host={sourceShellHost}
                value={sourceShellLogin?.credentialId ?? ''}
                onChange={setSourceShellCredentialId}
                disabled={!editable}
                missing={guestNode ? `No NKeeper host named ${guestNode} with an SSH login: NKeeper won’t check or move its storage.` : null}
              />
            ) : null}
            <fieldset className={styles.options} disabled={!editable}>
              <legend>How</legend>
              <Checkbox label="Move it while it runs (live), when its storage allows" checked={options.live} onChange={(event) => set({ live: event.target.checked })} />
              <Checkbox label="Start it on the new host after an offline move, if it was running" checked={options.startAfter} onChange={(event) => set({ startAfter: event.target.checked })} />
              <div className={fieldStyles.field}>
                <label className={fieldStyles.label} htmlFor="migrate-iscsi">
                  iSCSI on the old host afterwards
                </label>
                <select
                  id="migrate-iscsi"
                  className={listStyles.input}
                  value={options.logoutOldIscsi === null ? 'default' : options.logoutOldIscsi ? 'logout' : 'stay'}
                  onChange={(event) => set({ logoutOldIscsi: event.target.value === 'default' ? null : event.target.value === 'logout' })}
                >
                  <option value="default">{proxmox ? 'Stay logged in (default for Proxmox)' : 'Log out (default for KVM)'}</option>
                  <option value="stay">Stay logged in</option>
                  <option value="logout">Log out of the targets only this VM used</option>
                </select>
              </div>
              <div className={fieldStyles.field}>
                <label className={fieldStyles.label} htmlFor="migrate-drbd">
                  DRBD on the old host afterwards
                </label>
                <select id="migrate-drbd" className={listStyles.input} value={options.drbdDownOnOld ? 'down' : 'secondary'} onChange={(event) => set({ drbdDownOnOld: event.target.value === 'down' })}>
                  <option value="secondary">Stay Secondary (keeps replicating)</option>
                  <option value="down">Take the resource down</option>
                </select>
              </div>
              {proxmox ? (
                <Checkbox label="Copy disks on the node’s local storage" checked={options.withLocalDisks} onChange={(event) => set({ withLocalDisks: event.target.checked })} />
              ) : null}
            </fieldset>
          </>
        ) : null}

        {phase.kind === 'checking' ? (
          <div className={scanStyles.status}>
            <Spinner />
            <span>Checking both hosts and the VM’s storage…</span>
          </div>
        ) : null}

        {phase.kind === 'trust' ? (
          <div className={scanStyles.text}>
            <p>
              This is the first connection to{' '}
              {phase.role === 'source' ? (proxmox ? `${node.name}’s Proxmox API` : node.name) : phase.role === 'target' ? (targetHost?.name ?? 'the new host') : (sourceShellHost?.name ?? 'the node')}
              . Check its identity before NKeeper logs in:
            </p>
            <p className={scanStyles.fingerprint}>{phase.shown}</p>
          </div>
        ) : null}

        {plan ? <PlanView plan={plan} job={job} /> : null}
        {job && finished ? <Alert variant={job.state === 'succeeded' ? 'info' : 'error'}>{job.message}</Alert> : null}
        {phase.kind === 'running' && !finished ? (
          <div className={scanStyles.status}>
            <Spinner />
            <span>Migrating… Closing this doesn’t stop it; it runs on the NKeeper server.</span>
          </div>
        ) : null}
        {phase.kind === 'failed' ? <Alert>{phase.message}</Alert> : null}
      </div>
    </Dialog>
  )
}

function LoginPicker({
  id,
  label,
  host,
  value,
  onChange,
  disabled,
  missing,
}: {
  id: string
  label: string
  host: Host | undefined
  value: string
  onChange: (credentialId: string) => void
  disabled: boolean
  missing: string | null
}) {
  if (!host) {
    return missing ? <p className={scanStyles.muted}>{missing}</p> : null
  }
  return (
    <div className={fieldStyles.field}>
      <label className={fieldStyles.label} htmlFor={id}>
        {label}
      </label>
      <select id={id} className={listStyles.input} value={value} onChange={(event) => onChange(event.target.value)} disabled={disabled}>
        {usable(host).map((c) => (
          <option key={c.id} value={c.id}>
            {c.label ?? c.username}
          </option>
        ))}
      </select>
    </div>
  )
}

/** The plan: how it moves, its disks, what blocks it, and its steps (with their progress once it runs). */
function PlanView({ plan, job }: { plan: Plan; job: JobView | null }) {
  const host = (side: string) => (side === 'source' ? plan.sourceName : side === 'target' ? plan.targetName : side === 'both' ? 'both' : '')
  return (
    <div className={styles.plan}>
      <p className={scanStyles.text}>
        <strong>{plan.mode === 'live' ? 'Live' : 'Offline'}</strong>: {plan.sourceName} → {plan.targetName}. It is {plan.guestState}.
      </p>
      {plan.blockers.map((b) => (
        <Alert key={b}>{b}</Alert>
      ))}
      {plan.disks.length > 0 ? (
        <table className={styles.disks} aria-label="Disks">
          <thead>
            <tr>
              <th scope="col">Disk</th>
              <th scope="col">Storage</th>
              <th scope="col">On</th>
            </tr>
          </thead>
          <tbody>
            {plan.disks.map((disk) => (
              <tr key={disk.name}>
                <td>{disk.name}</td>
                <td>
                  {LAYOUTS[disk.layout]}
                  {disk.storage ? <div className={scanStyles.muted}>{disk.storage}</div> : null}
                </td>
                <td>
                  {chain(disk) || disk.path}
                  {disk.note ? <div className={scanStyles.muted}>{disk.note}</div> : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
      {plan.notes.length > 0 ? (
        <ul className={styles.notes}>
          {plan.notes.map((n) => (
            <li key={n}>{n}</li>
          ))}
        </ul>
      ) : null}
      {plan.choices.iscsiKept.length > 0 ? (
        <p className={scanStyles.muted}>Staying logged in on {plan.sourceName} because something else uses them: {plan.choices.iscsiKept.join('; ')}.</p>
      ) : null}
      <ol className={styles.steps} aria-label="Steps">
        {(job?.steps ?? plan.steps.map((s) => ({ title: s.title, side: s.side, state: 'pending', message: null, critical: s.critical }))).map((step, index) => (
          <li key={index} className={styles[step.state] ?? ''} data-state={step.state}>
            <span className={styles.icon} aria-hidden="true">
              {STEP_STATES[step.state] ?? '·'}
            </span>
            <span>
              {step.title}
              {host(step.side) && !step.title.includes(host(step.side)) ? <span className={scanStyles.muted}> · {host(step.side)}</span> : null}
              {!step.critical ? <span className={scanStyles.muted}> · optional</span> : null}
              {step.message ? <div className={scanStyles.muted}>{step.message}</div> : null}
            </span>
          </li>
        ))}
      </ol>
    </div>
  )
}
