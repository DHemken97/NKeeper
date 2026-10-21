import { useEffect, useRef, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Dialog } from '../../../components/ui/Dialog'
import { Spinner } from '../../../components/ui/Spinner'
import { api, ApiError } from '../../../lib/api'
import { PROXMOX_TASKS, type ProxmoxTask } from '../hostMenu'
import type { Host } from '../types'
import fieldStyles from './ImportHostsDialog.module.css'
import listStyles from './ListEditor.module.css'
import styles from './ScanDialog.module.css'

interface ProxmoxTaskDialogProps {
  /** The VM or container. */
  vm: Host
  /** The Proxmox node whose service and login are used. */
  node: Host
  serviceIndex: number
  credentialId?: string
  oneOff?: { username: string; password: string; domain?: string }
  task: ProxmoxTask
  onClose: () => void
}

interface Guest {
  vmId: number
  type: 'qemu' | 'lxc'
  node: string
  name: string
  status: string
}

interface TaskResponse {
  status: 'ready' | 'trustRequired'
  target: string | null
  certificate: { fingerprint: string; subject: string; expires: string } | null
  guest: Guest | null
  nodes: { name: string; online: boolean }[] | null
  upid: string | null
  task: { running: boolean; exitStatus: string | null } | null
}

type Phase =
  | { kind: 'loading' }
  | { kind: 'trust'; fingerprint: string; subject: string; target: string | null }
  | { kind: 'confirm'; guest: Guest; nodes: { name: string; online: boolean }[] }
  | { kind: 'running'; guest: Guest; upid: string | null }
  | { kind: 'done'; message: string }
  | { kind: 'failed'; message: string }

const POLL_MS = 2000

/** What each action does, for the confirmation. */
const EXPLANATIONS: Record<ProxmoxTask, string> = {
  migrate: 'Moves it to another node of the cluster.',
  shutdown: 'Asks its operating system to shut down cleanly. A VM needs to respond to ACPI (or have the guest agent).',
  reboot: 'Asks its operating system to restart cleanly.',
  pause: 'Freezes it in memory. Resume continues it where it was.',
  hibernate: 'Saves its memory to disk and stops it. Start resumes it where it was.',
  resume: 'Continues a paused VM or container.',
  start: 'Starts it (and resumes it, if it was hibernated).',
  stop: 'Turns it off at once, like pulling the plug. Anything unsaved is lost.',
  reset: 'Restarts it at once, like pressing the reset button. Anything unsaved is lost.',
}

/** What the done message says happened. */
const DONE: Record<ProxmoxTask, string> = {
  migrate: 'was moved',
  shutdown: 'was shut down',
  reboot: 'was rebooted',
  pause: 'was paused',
  hibernate: 'was hibernated',
  resume: 'was resumed',
  start: 'was started',
  stop: 'was stopped',
  reset: 'was reset',
}

/**
 * A Proxmox power action or migration for a VM or container, through its node's Proxmox API:
 * shows its current state, asks for confirmation (and, to migrate, the node to move it to), then
 * follows Proxmox's task until it ends.
 */
export function ProxmoxTaskDialog({ vm, node, serviceIndex, credentialId, oneOff, task, onClose }: ProxmoxTaskDialogProps) {
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' })
  const [target, setTarget] = useState('')
  const [online, setOnline] = useState(true)
  const [localDisks, setLocalDisks] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const trustRef = useRef<string | undefined>(undefined)
  const info = PROXMOX_TASKS[task]
  const vmId = Number(/\/(\d+)$/.exec(vm.virtualMachineId ?? '')?.[1])

  const body = () => ({
    hostId: node.id,
    serviceIndex,
    credentialId,
    username: oneOff?.username,
    password: oneOff?.password,
    trustFingerprint: trustRef.current,
    vm: vmId,
  })

  function failed(error: unknown, fallback: string): Phase {
    return { kind: 'failed', message: error instanceof ApiError ? error.message : fallback }
  }

  function trust(response: TaskResponse): Phase | null {
    return response.status === 'trustRequired' && response.certificate
      ? { kind: 'trust', fingerprint: response.certificate.fingerprint, subject: response.certificate.subject, target: response.target }
      : null
  }

  // The guest's current state, and the nodes to migrate to.
  useEffect(() => {
    let cancelled = false
    api.post<TaskResponse>('/api/proxmox/guest', { body: body() }).then(
      (response) => {
        if (cancelled) {
          return
        }
        const prompt = trust(response)
        if (prompt || !response.guest) {
          setPhase(prompt ?? { kind: 'failed', message: 'Proxmox didn’t describe this VM.' })
          return
        }
        const guest = response.guest
        const nodes = response.nodes ?? []
        setTarget(nodes.find((n) => n.online && n.name !== guest.node)?.name ?? '')
        setOnline(guest.status === 'running')
        setPhase({ kind: 'confirm', guest, nodes })
      },
      (error: unknown) => {
        if (!cancelled) {
          setPhase(failed(error, 'Couldn’t reach Proxmox.'))
        }
      },
    )
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reloads when the user trusts the certificate
  }, [attempt])

  // Follows the task until Proxmox says it ended.
  const running = phase.kind === 'running' ? phase : null
  useEffect(() => {
    if (!running?.upid) {
      return
    }
    let cancelled = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const poll = () => {
      api.post<TaskResponse>('/api/proxmox/task', { body: { ...body(), upid: running.upid } }).then(
        (response) => {
          if (cancelled) {
            return
          }
          if (response.task?.running !== false) {
            timer = setTimeout(poll, POLL_MS)
            return
          }
          const exit = response.task.exitStatus
          setPhase(
            exit === 'OK'
              ? { kind: 'done', message: `“${running.guest.name}” ${DONE[task]}${task === 'migrate' ? ` to ${target}` : ''}.` }
              : { kind: 'failed', message: `Proxmox says the task failed: ${exit ?? 'no reason given'}.` },
          )
        },
        (error: unknown) => {
          if (!cancelled) {
            setPhase(failed(error, 'Couldn’t follow the task. It may still be running in Proxmox.'))
          }
        },
      )
    }
    timer = setTimeout(poll, POLL_MS)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- one poll loop per task
  }, [running?.upid])

  function run(guest: Guest) {
    setPhase({ kind: 'running', guest, upid: null })
    const request =
      task === 'migrate'
        ? api.post<TaskResponse>('/api/proxmox/migrate', { body: { ...body(), target, online, withLocalDisks: localDisks } })
        : api.post<TaskResponse>('/api/proxmox/power', { body: { ...body(), action: task } })
    request.then(
      (response) => {
        const prompt = trust(response)
        setPhase(prompt ?? (response.upid ? { kind: 'running', guest, upid: response.upid } : { kind: 'failed', message: 'Proxmox didn’t start the task.' }))
      },
      (error: unknown) => setPhase(failed(error, `Couldn’t ${info.label.toLowerCase()} “${guest.name}”.`)),
    )
  }

  const guest = phase.kind === 'confirm' ? phase.guest : null
  const kind = guest?.type === 'lxc' ? 'container' : 'VM'
  const canMigrate = phase.kind !== 'confirm' || task !== 'migrate' || target !== ''

  return (
    <Dialog
      title={`${info.label} ${vm.name}`}
      description={`Through Proxmox on ${node.name}.`}
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            {phase.kind === 'confirm' || phase.kind === 'trust' || phase.kind === 'loading' ? 'Cancel' : 'Close'}
          </Button>
          {phase.kind === 'trust' ? (
            <Button
              onClick={() => {
                trustRef.current = phase.fingerprint
                setPhase({ kind: 'loading' })
                setAttempt((n) => n + 1)
              }}
            >
              Trust and continue
            </Button>
          ) : null}
          {guest ? (
            <Button variant={info.danger ? 'danger' : 'primary'} onClick={() => run(guest)} disabled={!canMigrate}>
              {info.label}
            </Button>
          ) : null}
        </>
      }
    >
      {phase.kind === 'loading' ? (
        <div className={styles.status}>
          <Spinner />
          <span>Asking Proxmox about {vm.name}…</span>
        </div>
      ) : null}
      {phase.kind === 'trust' ? (
        <div className={styles.text}>
          <p>This is the first connection to {phase.target ?? node.name}. Check its certificate before NKeeper signs in:</p>
          <p className={styles.fingerprint}>{phase.fingerprint}</p>
          <p className={styles.muted}>Issued to {phase.subject}. It’s then trusted for the node’s web interface too.</p>
        </div>
      ) : null}
      {guest && phase.kind === 'confirm' ? (
        <div className={fieldStyles.form}>
          <p className={styles.text}>
            {guest.name} is a {kind} ({guest.vmId}) on {guest.node}, currently <strong>{guest.status}</strong>. {EXPLANATIONS[task]}
          </p>
          {task === 'migrate' ? (
            <>
              <div className={fieldStyles.field}>
                <label className={fieldStyles.label} htmlFor="proxmox-target">
                  Move to
                </label>
                <select id="proxmox-target" className={listStyles.input} value={target} onChange={(event) => setTarget(event.target.value)}>
                  {phase.nodes.every((n) => n.name === guest.node || !n.online) ? <option value="">No other node is online</option> : null}
                  {phase.nodes
                    .filter((n) => n.name !== guest.node)
                    .map((n) => (
                      <option key={n.name} value={n.name} disabled={!n.online}>
                        {n.online ? n.name : `${n.name} (offline)`}
                      </option>
                    ))}
                </select>
              </div>
              {guest.status === 'running' ? (
                <Checkbox
                  label={guest.type === 'lxc' ? 'Restart it on the new node (containers can’t move while running)' : 'Move it while it runs (live migration)'}
                  checked={online}
                  onChange={(event) => setOnline(event.target.checked)}
                />
              ) : null}
              {guest.type === 'qemu' ? (
                <Checkbox label="Copy disks on the node’s local storage too" checked={localDisks} onChange={(event) => setLocalDisks(event.target.checked)} />
              ) : null}
            </>
          ) : null}
        </div>
      ) : null}
      {phase.kind === 'running' ? (
        <div className={styles.status}>
          <Spinner />
          <span>
            {phase.upid ? 'Proxmox is working on it… Closing this doesn’t stop the task.' : 'Starting…'}
          </span>
        </div>
      ) : null}
      {phase.kind === 'done' ? <Alert variant="info">{phase.message}</Alert> : null}
      {phase.kind === 'failed' ? <Alert>{phase.message}</Alert> : null}
    </Dialog>
  )
}
