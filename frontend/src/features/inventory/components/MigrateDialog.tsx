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
    /** Whether NKeeper could see the disks (Proxmox needs SSH logins to the nodes for that). */
    storageChecked: boolean
    /** Whether the disks on local storage are copied (the default, when the user didn't choose). */
    copyLocalDisks: boolean
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
  /** null: the default (copy for KVM, don't for Proxmox). */
  withLocalDisks: boolean | null
  /** KVM: delete the old copies of copied disks on the old host afterwards. */
  deleteOldLocalDisks: boolean
}

type Phase =
  | { kind: 'setup' }
  /** Detecting the storage; `previous` stays shown while an option change is checked again. */
  | { kind: 'checking'; previous?: Plan }
  | { kind: 'trust'; role: Role; fingerprint: string; shown: string }
  | { kind: 'plan'; plan: Plan }
  /** The final review: every step, exactly, before anything happens. */
  | { kind: 'confirm'; plan: Plan }
  | { kind: 'running'; plan: Plan; jobId: string; job: JobView | null }
  | { kind: 'failed'; message: string }

const POLL_MS = 1500

/** A layer in words, e.g. "DRBD resource r0" or "iSCSI target iqn… at 10.0.0.5:3260". */
function layerName(layer: StorageLayer): string {
  switch (layer.kind) {
    case 'drbd':
      return `DRBD resource ${layer.name}`
    case 'lvm':
      return `LVM volume ${layer.name}`
    case 'multipath':
      return `multipath device ${layer.detail ?? layer.name}`
    case 'iscsi':
      return `iSCSI target ${layer.name}${layer.detail ? ` at ${layer.detail}` : ''}`
    default:
      return `local disk ${layer.name}`
  }
}

/** What a disk is, as NKeeper found it, e.g. "An image file on a file system on DRBD resource r0". */
function describeDisk(disk: GuestDisk, sourceName: string): string {
  const top = disk.layers[0]
  const fs = disk.mount ? ` (${disk.mount.fsType} at ${disk.mount.mountPoint})` : ''
  switch (disk.layout) {
    case 'local':
      return disk.mount ? `An image file on ${sourceName}’s own disk${fs}` : `${sourceName}’s own disk${disk.storage ? ` (Proxmox storage ${disk.storage})` : ''}`
    case 'network':
      return disk.mount ? `An image file on network storage${fs}` : `Network storage the hypervisor reaches by itself${disk.storage ? ` (${disk.storage})` : ''}`
    case 'shared':
      return `An image file on a cluster file system${fs}`
    case 'filesystem':
      return `An image file on a file system${fs} on ${top ? layerName(top) : 'shared storage'}`
    case 'block':
      return top ? `${layerName(top)[0].toUpperCase()}${layerName(top).slice(1)}, used directly as the disk` : 'A shared block device'
    case 'managed':
      return `Proxmox storage ${disk.storage ?? ''}, which Proxmox activates on the new node${disk.layers.some((l) => l.kind === 'iscsi') ? ' (over iSCSI)' : ''}`
  }
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
  const [options, setOptions] = useState<Options>({ live: true, startAfter: true, logoutOldIscsi: null, drbdDownOnOld: false, withLocalDisks: null, deleteOldLocalDisks: false })
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
    options: { ...options, logoutOldIscsi: options.logoutOldIscsi ?? undefined, withLocalDisks: options.withLocalDisks ?? undefined },
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
    setPhase((current) => ({ kind: 'checking', previous: current.kind === 'plan' ? current.plan : undefined }))
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
    api.post<MigrationResponse>('/api/migrations', { body: { ...body(), confirmedSteps: plan.steps.map((s) => s.title) } }).then(
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
  const plan = phase.kind === 'plan' || phase.kind === 'confirm' || phase.kind === 'running' ? phase.plan : phase.kind === 'checking' ? (phase.previous ?? null) : null
  const job = phase.kind === 'running' ? phase.job : null
  const finished = job !== null && job.state !== 'running'
  const canCheck = proxmox ? targetNode !== '' : targetLogin !== null
  // Where it goes is chosen before detecting; changing it means detecting again.
  const choosing = phase.kind === 'setup' || (phase.kind === 'checking' && !phase.previous)

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
              Detect storage
            </Button>
          ) : null}
          {phase.kind === 'plan' ? (
            <Button onClick={() => setPhase({ kind: 'confirm', plan: phase.plan })} disabled={phase.plan.blockers.length > 0}>
              Review steps…
            </Button>
          ) : null}
          {phase.kind === 'confirm' ? (
            <>
              <Button variant="secondary" onClick={() => setPhase({ kind: 'plan', plan: phase.plan })}>
                Back
              </Button>
              <Button variant={phase.plan.mode === 'offline' && phase.plan.guestState === 'running' ? 'danger' : 'primary'} onClick={() => start(phase.plan)}>
                {phase.plan.mode === 'live' ? 'Migrate live' : phase.plan.guestState === 'running' ? 'Shut down and migrate' : 'Migrate'}
              </Button>
            </>
          ) : null}
        </>
      }
    >
      <div className={fieldStyles.form}>
        {choosing ? (
          <>
            <div className={fieldStyles.field}>
              <label className={fieldStyles.label} htmlFor="migrate-target">
                Move to
              </label>
              {proxmox ? (
                <select id="migrate-target" className={listStyles.input} value={targetNode} onChange={(event) => setTargetNode(event.target.value)} disabled={phase.kind !== 'setup'}>
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
                <select id="migrate-target" className={listStyles.input} value={targetHostId} onChange={(event) => setTargetHostId(event.target.value)} disabled={phase.kind !== 'setup'}>
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
              disabled={phase.kind !== 'setup'}
              missing={proxmox && targetNode ? `No NKeeper host named ${targetNode} with an SSH login: NKeeper won’t check or move its storage.` : null}
            />
            {proxmox ? (
              <LoginPicker
                id="migrate-source-login"
                label={`SSH login to ${guestNode ?? 'the current node'} (for storage)`}
                host={sourceShellHost}
                value={sourceShellLogin?.credentialId ?? ''}
                onChange={setSourceShellCredentialId}
                disabled={phase.kind !== 'setup'}
                missing={guestNode ? `No NKeeper host named ${guestNode} with an SSH login: NKeeper won’t check or move its storage.` : null}
              />
            ) : null}
          </>
        ) : null}

        {plan && phase.kind !== 'running' && phase.kind !== 'confirm' ? (
          <p className={scanStyles.text}>
            {plan.sourceName} → {plan.targetName}.{' '}
            <Button variant="ghost" size="sm" onClick={() => setPhase({ kind: 'setup' })} disabled={phase.kind !== 'plan'}>
              Change
            </Button>
          </p>
        ) : null}
        {plan && phase.kind !== 'confirm' ? <StorageFound plan={plan} /> : null}
        {plan && phase.kind !== 'running' && phase.kind !== 'confirm' ? <RelevantOptions plan={plan} options={options} set={set} proxmox={proxmox} disabled={phase.kind !== 'plan'} /> : null}

        {phase.kind === 'checking' ? (
          <div className={scanStyles.status}>
            <Spinner />
            <span>{phase.previous ? 'Checking again…' : 'Detecting the VM’s storage on both hosts…'}</span>
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

        {phase.kind === 'confirm' ? <Review plan={phase.plan} vm={vm.name} /> : null}
        {plan && phase.kind !== 'confirm' ? <PlanView plan={plan} job={job} steps={phase.kind === 'running'} /> : null}
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

/** What NKeeper found each disk to be, and whether there's any storage to hand over. */
function StorageFound({ plan }: { plan: Plan }) {
  if (!plan.choices.storageChecked) {
    return <p className={scanStyles.muted}>NKeeper didn’t look at the storage: it needs an SSH login to both nodes for that. Proxmox checks its own storage when it moves the VM.</p>
  }
  const handover = plan.disks.some((d) => d.layout === 'filesystem' || d.layout === 'block') || plan.choices.hasIscsi
  return (
    <div className={styles.plan}>
      <table className={styles.disks} aria-label="Disks">
        <thead>
          <tr>
            <th scope="col">Disk</th>
            <th scope="col">What it is</th>
            <th scope="col">Path</th>
          </tr>
        </thead>
        <tbody>
          {plan.disks.map((disk) => (
            <tr key={disk.name}>
              <td>{disk.name}</td>
              <td>
                {describeDisk(disk, plan.sourceName)}
                {disk.layers.length > 1 ? <div className={scanStyles.muted}>{chain(disk)}</div> : null}
                {disk.note ? <div className={scanStyles.muted}>{disk.note}</div> : null}
              </td>
              <td className={styles.path}>{disk.path}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {plan.disks.length === 0 ? <p className={scanStyles.muted}>It has no disks.</p> : null}
      {plan.disks.length > 0 && !handover ? <p className={scanStyles.muted}>There’s no storage to hand over: {plan.targetName} reaches it as it is.</p> : null}
    </div>
  )
}

/** Only the options that mean something for this VM and its storage. */
function RelevantOptions({
  plan,
  options,
  set,
  proxmox,
  disabled,
}: {
  plan: Plan
  options: Options
  set: (change: Partial<Options>) => void
  proxmox: boolean
  disabled: boolean
}) {
  const running = plan.guestState === 'running'
  const { choices } = plan
  // KVM: copyable image files were found; Proxmox: local disks, or the storage couldn't be looked at.
  const copyLocal = choices.hasLocalDisks || (proxmox && !choices.storageChecked)
  if (!running && !choices.hasIscsi && !choices.hasDrbd && !copyLocal) {
    return null
  }
  return (
    <fieldset className={styles.options} disabled={disabled}>
      <legend>Options</legend>
      {running && choices.liveAvailable ? (
        <Checkbox label="Move it while it runs (live)" checked={options.live} onChange={(event) => set({ live: event.target.checked })} />
      ) : null}
      {running && !choices.liveAvailable ? <p className={scanStyles.muted}>It can’t move while it runs, so it will be shut down: {choices.liveUnavailableReason}</p> : null}
      {running && plan.mode === 'offline' ? (
        <Checkbox label={`Start it on ${plan.targetName} afterwards`} checked={options.startAfter} onChange={(event) => set({ startAfter: event.target.checked })} />
      ) : null}
      {choices.hasIscsi ? (
        <div className={fieldStyles.field}>
          <label className={fieldStyles.label} htmlFor="migrate-iscsi">
            iSCSI on {plan.sourceName} afterwards
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
          {choices.logoutOldIscsi && choices.iscsiToLogOut.length > 0 ? <p className={scanStyles.muted}>Logs out of {choices.iscsiToLogOut.join('; ')}.</p> : null}
          {choices.iscsiKept.length > 0 ? <p className={scanStyles.muted}>Stays logged in to {choices.iscsiKept.join('; ')}.</p> : null}
        </div>
      ) : null}
      {choices.hasDrbd ? (
        <div className={fieldStyles.field}>
          <label className={fieldStyles.label} htmlFor="migrate-drbd">
            DRBD on {plan.sourceName} afterwards
          </label>
          <select id="migrate-drbd" className={listStyles.input} value={options.drbdDownOnOld ? 'down' : 'secondary'} onChange={(event) => set({ drbdDownOnOld: event.target.value === 'down' })}>
            <option value="secondary">Stay Secondary (keeps replicating)</option>
            <option value="down">Take the resource down</option>
          </select>
        </div>
      ) : null}
      {copyLocal ? (
        <Checkbox
          label={proxmox ? 'Copy disks on the node’s local storage' : `Copy its local disks to ${plan.targetName}`}
          checked={options.withLocalDisks ?? choices.copyLocalDisks}
          onChange={(event) => set({ withLocalDisks: event.target.checked })}
        />
      ) : null}
      {copyLocal && !proxmox && choices.copyLocalDisks ? (
        <Checkbox
          label={`Delete the old copies on ${plan.sourceName} afterwards`}
          checked={options.deleteOldLocalDisks}
          onChange={(event) => set({ deleteOldLocalDisks: event.target.checked })}
        />
      ) : null}
    </fieldset>
  )
}

/** The last word before anything happens: every step in order, on which host, and what happens if one fails. */
function Review({ plan, vm }: { plan: Plan; vm: string }) {
  const shutdown = plan.steps.findIndex((s) => s.kind === 'guest-shutdown')
  const start = plan.steps.findIndex((s) => s.kind === 'guest-start')
  const required = plan.steps.filter((s) => s.critical).length
  return (
    <div className={styles.plan}>
      <p className={scanStyles.text}>
        <strong>
          {plan.mode === 'live' ? 'Live migration' : 'Offline migration'} of {vm} from {plan.sourceName} to {plan.targetName}.
        </strong>{' '}
        NKeeper will do exactly this, in this order, and nothing else:
      </p>
      <ol className={styles.review} aria-label="Steps to confirm">
        {plan.steps.map((step, index) => (
          <li key={index}>
            {step.title}
            {!step.critical ? <span className={scanStyles.muted}> (tidying up: if this fails, the migration still counts as done)</span> : null}
          </li>
        ))}
      </ol>
      {shutdown !== -1 ? (
        <Alert>
          {vm} is off from step {shutdown + 1}
          {start !== -1 ? ` until step ${start + 1}` : ' and stays off afterwards'}.
        </Alert>
      ) : null}
      <p className={scanStyles.muted}>
        If any of the {required} required {required === 1 ? 'step fails' : 'steps fails'}, NKeeper stops there and undoes the steps it already did, newest first, so
        everything is back on {plan.sourceName} as it was{shutdown !== -1 ? `, and ${vm} is started there again` : ''}.
      </p>
    </div>
  )
}

/** What blocks the move, notes, and (once it runs) its steps and their progress. */
function PlanView({ plan, job, steps }: { plan: Plan; job: JobView | null; steps: boolean }) {
  const host = (side: string) => (side === 'source' ? plan.sourceName : side === 'target' ? plan.targetName : side === 'both' ? 'both' : '')
  return (
    <div className={styles.plan}>
      <p className={scanStyles.text}>
        <strong>{plan.mode === 'live' ? 'Live' : 'Offline'}</strong>: {plan.sourceName} → {plan.targetName}. It is {plan.guestState}.
      </p>
      {plan.blockers.map((b) => (
        <Alert key={b}>{b}</Alert>
      ))}
      {plan.notes.length > 0 ? (
        <ul className={styles.notes}>
          {plan.notes.map((n) => (
            <li key={n}>{n}</li>
          ))}
        </ul>
      ) : null}
      {steps ? (
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
      ) : null}
    </div>
  )
}
