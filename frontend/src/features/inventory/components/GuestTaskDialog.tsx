import { useEffect, useRef, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { Spinner } from '../../../components/ui/Spinner'
import { api, ApiError } from '../../../lib/api'
import type { VirtualMachine } from '../../terminal/kvm'
import { GUEST_TASKS, guestId, type GuestTask, type GuestVia } from '../hostMenu'
import type { Host } from '../types'
import fieldStyles from './ImportHostsDialog.module.css'
import styles from './ScanDialog.module.css'

interface GuestTaskDialogProps {
  /** The VM or container. */
  vm: Host
  /** The hypervisor whose service and login are used. */
  node: Host
  serviceIndex: number
  via: GuestVia
  credentialId?: string
  oneOff?: { username: string; password: string; domain?: string }
  task: GuestTask
  onClose: () => void
}

/** The VM as either hypervisor describes it. */
interface Guest {
  /** The Proxmox VM ID; null for libvirt. */
  vmId: number | null
  type: 'qemu' | 'lxc'
  node: string
  name: string
  status: string
}

interface ProxmoxResponse {
  status: 'ready' | 'trustRequired'
  target: string | null
  certificate: { fingerprint: string; subject: string; expires: string } | null
  guest: Guest | null
  nodes: { name: string; online: boolean }[] | null
  upid: string | null
  task: { running: boolean; exitStatus: string | null } | null
}

interface KvmResponse {
  status: 'ready' | 'trustRequired'
  vm: VirtualMachine | null
  hostKey: { algorithm: string; fingerprint: string } | null
}

/** A key or certificate to trust before NKeeper connects. */
interface TrustPrompt {
  fingerprint: string
  /** How the fingerprint is shown, e.g. "ssh-ed25519 SHA256:…". */
  shown: string
  what: 'certificate' | 'host key'
  note: string
}

type Phase =
  | { kind: 'loading' }
  | { kind: 'trust'; prompt: TrustPrompt; target: string }
  | { kind: 'confirm'; guest: Guest; nodes: { name: string; online: boolean }[] }
  | { kind: 'running'; guest: Guest; upid: string | null }
  | { kind: 'done'; message: string }
  | { kind: 'failed'; message: string }

const POLL_MS = 2000
/** How long to wait for a libvirt VM to finish shutting down. */
const SHUTDOWN_WAIT_MS = 3 * 60 * 1000

/** What each action does, for the confirmation. */
const EXPLANATIONS: Record<GuestTask, string> = {
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
const DONE: Record<GuestTask, string> = {
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

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

/**
 * A power action for a VM or container, through its hypervisor: the Proxmox API for a Proxmox VM,
 * or virsh over SSH for a libvirt one. Shows its current state, asks for confirmation, then follows
 * the task until it ends. Migrations have their own dialog (MigrateDialog), which moves storage too.
 */
export function GuestTaskDialog({ vm, node, serviceIndex, via, credentialId, oneOff, task, onClose }: GuestTaskDialogProps) {
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' })
  const [attempt, setAttempt] = useState(0)
  const trustRef = useRef<string | undefined>(undefined)
  const closedRef = useRef(false)
  const info = GUEST_TASKS[task]
  const id = guestId(vm)?.id ?? ''

  useEffect(() => () => void (closedRef.current = true), [])

  const body = () => ({
    hostId: node.id,
    serviceIndex,
    credentialId,
    username: oneOff?.username,
    password: oneOff?.password,
    trustFingerprint: trustRef.current,
    vm: via === 'proxmox' ? Number(id) : id,
  })

  function failed(error: unknown, fallback: string): Phase {
    return { kind: 'failed', message: error instanceof ApiError ? error.message : fallback }
  }

  /** The prompt when the answer asks to trust the hypervisor first. */
  function trustPhase(response: ProxmoxResponse | KvmResponse): Phase | null {
    if (response.status !== 'trustRequired') {
      return null
    }
    if ('certificate' in response && response.certificate) {
      const { fingerprint, subject } = response.certificate
      return {
        kind: 'trust',
        target: response.target ?? node.name,
        prompt: { fingerprint, shown: fingerprint, what: 'certificate', note: `Issued to ${subject}. It’s then trusted for the node’s web interface too.` },
      }
    }
    if ('hostKey' in response && response.hostKey) {
      const { algorithm, fingerprint } = response.hostKey
      return {
        kind: 'trust',
        target: node.name,
        prompt: { fingerprint, shown: `${algorithm} SHA256:${fingerprint}`, what: 'host key', note: 'The key is then trusted for SSH sessions too.' },
      }
    }
    return null
  }

  /** A libvirt VM as a guest. */
  const kvmGuest = (machine: VirtualMachine): Guest => ({ vmId: null, type: 'qemu', node: node.name, name: machine.name, status: machine.state })

  // The guest's current state, and the nodes to migrate to.
  useEffect(() => {
    let cancelled = false
    const load =
      via === 'proxmox'
        ? api.post<ProxmoxResponse>('/api/proxmox/guest', { body: body() }).then((response) => {
            const prompt = trustPhase(response)
            if (prompt || !response.guest) {
              return prompt ?? ({ kind: 'failed', message: 'Proxmox didn’t describe this VM.' } as Phase)
            }
            return { kind: 'confirm', guest: response.guest, nodes: response.nodes ?? [] } as Phase
          })
        : api.post<KvmResponse>('/api/kvm/vm', { body: body() }).then((response) => {
            const prompt = trustPhase(response)
            if (prompt || !response.vm) {
              return prompt ?? ({ kind: 'failed', message: 'libvirt didn’t describe this VM.' } as Phase)
            }
            return { kind: 'confirm', guest: kvmGuest(response.vm), nodes: [] } as Phase
          })
    load.then(
      (next) => {
        if (cancelled) {
          return
        }
        setPhase(next)
      },
      (error: unknown) => {
        if (!cancelled) {
          setPhase(failed(error, via === 'proxmox' ? 'Couldn’t reach Proxmox.' : 'Couldn’t reach the KVM host.'))
        }
      },
    )
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reloads when the user trusts the key or certificate
  }, [attempt])

  // Proxmox: follows the task until Proxmox says it ended.
  const running = phase.kind === 'running' ? phase : null
  useEffect(() => {
    if (!running?.upid) {
      return
    }
    let cancelled = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const poll = () => {
      api.post<ProxmoxResponse>('/api/proxmox/task', { body: { ...body(), upid: running.upid } }).then(
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
              ? { kind: 'done', message: `“${running.guest.name}” ${DONE[task]}.` }
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

  /** libvirt: a shutdown returns at once; waits until the VM is off. */
  async function waitForShutOff(guest: Guest): Promise<Phase> {
    const deadline = Date.now() + SHUTDOWN_WAIT_MS
    while (Date.now() < deadline) {
      await sleep(POLL_MS)
      if (closedRef.current) {
        return { kind: 'failed', message: 'Closed.' }
      }
      const response = await api.post<KvmResponse>('/api/kvm/vm', { body: body() })
      if (response.vm?.state === 'shut off') {
        return { kind: 'done', message: `“${guest.name}” ${DONE.shutdown}.` }
      }
    }
    return { kind: 'failed', message: `“${guest.name}” is still running after 3 minutes. Its operating system may be ignoring the request: try again, or use Stop.` }
  }

  async function runKvm(guest: Guest): Promise<Phase> {
    const response = await api.post<KvmResponse>('/api/kvm/power', { body: { ...body(), action: task } })
    const prompt = trustPhase(response)
    if (prompt) {
      return prompt
    }
    if (task === 'shutdown') {
      return waitForShutOff(guest)
    }
    const state = response.vm?.state
    return { kind: 'done', message: `“${guest.name}” ${DONE[task]}.${state ? ` It’s now ${state}.` : ''}` }
  }

  function run(guest: Guest) {
    setPhase({ kind: 'running', guest, upid: null })
    const request: Promise<Phase> =
      via === 'kvm'
        ? runKvm(guest)
        : api.post<ProxmoxResponse>('/api/proxmox/power', { body: { ...body(), action: task } }).then(
            (response) =>
              trustPhase(response) ??
              (response.upid ? { kind: 'running', guest, upid: response.upid } : { kind: 'failed', message: 'Proxmox didn’t start the task.' }),
          )
    request.then(
      (next) => {
        if (!closedRef.current) {
          setPhase(next)
        }
      },
      (error: unknown) => {
        if (!closedRef.current) {
          setPhase(failed(error, `Couldn’t ${info.label.toLowerCase()} “${guest.name}”.`))
        }
      },
    )
  }

  const guest = phase.kind === 'confirm' ? phase.guest : null
  const kind = guest?.type === 'lxc' ? 'container' : 'VM'

  return (
    <Dialog
      title={`${info.label} ${vm.name}`}
      description={via === 'proxmox' ? `Through Proxmox on ${node.name}.` : `Through libvirt on ${node.name}.`}
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            {phase.kind === 'confirm' || phase.kind === 'trust' || phase.kind === 'loading' ? 'Cancel' : 'Close'}
          </Button>
          {phase.kind === 'trust' ? (
            <Button
              onClick={() => {
                trustRef.current = phase.prompt.fingerprint
                setPhase({ kind: 'loading' })
                setAttempt((n) => n + 1)
              }}
            >
              Trust and continue
            </Button>
          ) : null}
          {guest ? (
            <Button variant={info.danger ? 'danger' : 'primary'} onClick={() => run(guest)}>
              {info.label}
            </Button>
          ) : null}
        </>
      }
    >
      {phase.kind === 'loading' ? (
        <div className={styles.status}>
          <Spinner />
          <span>Asking {via === 'proxmox' ? 'Proxmox' : 'libvirt'} about {vm.name}…</span>
        </div>
      ) : null}
      {phase.kind === 'trust' ? (
        <div className={styles.text}>
          <p>
            This is the first connection to {phase.target}. Check its {phase.prompt.what} before NKeeper signs in:
          </p>
          <p className={styles.fingerprint}>{phase.prompt.shown}</p>
          <p className={styles.muted}>{phase.prompt.note}</p>
        </div>
      ) : null}
      {guest && phase.kind === 'confirm' ? (
        <div className={fieldStyles.form}>
          <p className={styles.text}>
            {guest.name} is a {kind}
            {guest.vmId !== null ? ` (${guest.vmId})` : ''} on {guest.node}, currently <strong>{guest.status}</strong>. {EXPLANATIONS[task]}
          </p>
        </div>
      ) : null}
      {phase.kind === 'running' ? (
        <div className={styles.status}>
          <Spinner />
          <span>
            {via === 'proxmox' && phase.upid
              ? 'Proxmox is working on it… Closing this doesn’t stop the task.'
              : task === 'shutdown' && via === 'kvm'
                ? 'Waiting for it to shut down… Closing this doesn’t stop the shutdown.'
                : 'Working on it…'}
          </span>
        </div>
      ) : null}
      {phase.kind === 'done' ? <Alert variant="info">{phase.message}</Alert> : null}
      {phase.kind === 'failed' ? <Alert>{phase.message}</Alert> : null}
    </Dialog>
  )
}
