import { useEffect, useMemo, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Dialog } from '../../../components/ui/Dialog'
import { Spinner } from '../../../components/ui/Spinner'
import { ApiError } from '../../../lib/api'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../api/inventoryApi'
import { toHostDraft, toHostInput } from '../drafts'
import { scanProposals, type ScanProposal } from '../scanProposals'
import { formatMemory, PROTOCOL_INFO, primaryAddress, type Host, type InternalScanLogin, type ScanFindings, type ScannedGuest } from '../types'
import styles from './ScanDialog.module.css'

interface ScanDialogProps {
  host: Host
  kind: 'internal' | 'external'
  /** Internal scans: how to log in. */
  login?: InternalScanLogin
  onClose: () => void
  /** Each host the chosen findings changed or added (the scanned host, VMs linked to it, new hosts). */
  onApplied: (host: Host) => void
  /** Administrators can add hosts for VMs that aren't in NKeeper yet. */
  canAddHosts: boolean
}

type Phase =
  | { kind: 'confirm' }
  | { kind: 'scanning' }
  | { kind: 'trust'; algorithm: string; fingerprint: string }
  | { kind: 'results'; findings: ScanFindings }
  | { kind: 'failed'; message: string }

/**
 * Tasks ▸ Scan: runs an internal (one SSH login) or external (network) scan, then lists what it
 * found as changes to pick from. Only the chosen changes are saved, as an ordinary host update.
 */
export function ScanDialog({ host, kind, login, onClose, onApplied, canAddHosts }: ScanDialogProps) {
  // An external scan is visible on the network, so it waits for a go-ahead.
  const [phase, setPhase] = useState<Phase>({ kind: kind === 'external' ? 'confirm' : 'scanning' })
  const [trust, setTrust] = useState<string | undefined>(undefined)
  const [attempt, setAttempt] = useState(0)
  const running = phase.kind === 'scanning'

  useEffect(() => {
    if (!running) {
      return
    }
    let cancelled = false
    const scan = kind === 'internal' ? hostsApi.scanInternal(host.id, login!, trust) : hostsApi.scanExternal(host.id)
    scan.then(
      (response) => {
        if (cancelled) {
          return
        }
        if (response.status === 'trustRequired' && response.hostKey) {
          setPhase({ kind: 'trust', ...response.hostKey })
        } else if (response.findings) {
          setPhase({ kind: 'results', findings: response.findings })
        }
      },
      (error: unknown) => {
        if (!cancelled) {
          setPhase({ kind: 'failed', message: error instanceof ApiError ? error.message : 'The scan failed.' })
        }
      },
    )
    return () => {
      cancelled = true
    }
    // `attempt` re-runs the scan (e.g. after trusting the host key).
  }, [running, attempt, kind, host.id, login, trust])

  function start() {
    setPhase({ kind: 'scanning' })
    setAttempt((n) => n + 1)
  }

  const title = kind === 'internal' ? `Internal scan of ${host.name}` : `External scan of ${host.name}`
  const scanService = login ? host.services[login.serviceIndex] : undefined
  const sshPort = scanService?.protocol === 'Ssh' ? (scanService.port ?? PROTOCOL_INFO.Ssh.defaultPort ?? 22) : null

  if (phase.kind === 'results') {
    return (
      <ScanResults
        title={title}
        host={host}
        findings={phase.findings}
        sshPort={sshPort}
        canAddHosts={canAddHosts}
        onClose={onClose}
        onApplied={onApplied}
      />
    )
  }

  return (
    <Dialog
      title={title}
      description={kind === 'internal' ? 'One login that reads the host’s own settings.' : `From the NKeeper server to ${primaryAddress(host)}.`}
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            {phase.kind === 'failed' ? 'Close' : 'Cancel'}
          </Button>
          {phase.kind === 'confirm' ? <Button onClick={start}>Start scan</Button> : null}
          {phase.kind === 'trust' ? (
            <Button
              onClick={() => {
                setTrust(phase.fingerprint)
                start()
              }}
            >
              Trust and scan
            </Button>
          ) : null}
          {phase.kind === 'failed' ? <Button onClick={start}>Try again</Button> : null}
        </>
      }
    >
      {phase.kind === 'confirm' ? (
        <p className={styles.text}>
          NKeeper will try common ports (SSH, RDP, VNC, web, file sharing, databases…) on this host and read what it can: open ports, the SSH
          banner, reverse DNS, and the MAC address if the host is on the server’s network segment. Network monitoring will see this as a
          port scan. To avoid that, use an internal scan, which is a single SSH login.
        </p>
      ) : null}
      {phase.kind === 'scanning' ? (
        <div className={styles.status}>
          <Spinner />
          <span>{kind === 'internal' ? 'Logging in and reading the host’s settings…' : 'Probing ports…'}</span>
        </div>
      ) : null}
      {phase.kind === 'trust' ? (
        <div className={styles.text}>
          <p>This is the first SSH connection to {host.name}. Check its host key before NKeeper logs in:</p>
          <p className={styles.fingerprint}>
            {phase.algorithm} SHA256:{phase.fingerprint}
          </p>
          <p className={styles.muted}>The key is then trusted for SSH sessions too.</p>
        </div>
      ) : null}
      {phase.kind === 'failed' ? <Alert>{phase.message}</Alert> : null}
    </Dialog>
  )
}

interface ScanResultsProps {
  title: string
  host: Host
  findings: ScanFindings
  sshPort: number | null
  canAddHosts: boolean
  onClose: () => void
  onApplied: (host: Host) => void
}

const GROUPS: { group: ScanProposal['group']; title: string }[] = [
  { group: 'Host', title: 'Host details' },
  { group: 'Services', title: 'Services' },
  { group: 'Logins', title: 'Logins' },
]

function ScanResults({ title, host, findings, sshPort, canAddHosts, onClose, onApplied }: ScanResultsProps) {
  const proposals = useMemo(() => scanProposals(host, findings, sshPort), [host, findings, sshPort])
  const [chosen, setChosen] = useState<ReadonlySet<string>>(() => new Set(proposals.filter((p) => p.checked).map((p) => p.key)))
  const links = useMemo(() => findings.guests.filter((g) => g.linkProposed && g.guestHostId && g.hypervisorHostId), [findings])
  const [chosenLinks, setChosenLinks] = useState<ReadonlySet<string>>(() => new Set(links.map((g) => g.virtualMachineId)))
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const otherPorts = findings.ports.filter((p) => p.protocol === null)
  // Guests NKeeper doesn't have can be added, unticked: adding hosts is a bigger step than linking.
  const addable = canAddHosts ? findings.guests.filter((g) => g.newHost && g.hypervisorHostId) : []
  const [chosenNew, setChosenNew] = useState<ReadonlySet<string>>(new Set())
  const unlinked = findings.guests.filter((g) => !links.includes(g) && !addable.includes(g))
  const changeCount = chosen.size + chosenLinks.size + chosenNew.size
  const anything = proposals.length + links.length + addable.length > 0

  function toggleLink(id: string, on: boolean) {
    setChosenLinks((current) => {
      const next = new Set(current)
      if (on) {
        next.add(id)
      } else {
        next.delete(id)
      }
      return next
    })
  }

  function toggle(key: string, on: boolean) {
    setChosen((current) => {
      const next = new Set(current)
      if (on) {
        next.add(key)
      } else {
        next.delete(key)
      }
      return next
    })
  }

  async function apply() {
    const draft = toHostDraft(host)
    proposals.filter((p) => chosen.has(p.key)).forEach((p) => p.apply(draft))
    setSaving(true)
    setError(null)
    try {
      if (chosen.size > 0) {
        onApplied(await hostsApi.update(host.id, toHostInput(draft, host.version)))
      }
      const chosenGuests = links.filter((g) => chosenLinks.has(g.virtualMachineId))
      if (chosenGuests.length > 0) {
        const updated = await hostsApi.linkVirtualMachines(
          chosenGuests.map((g) => ({ guestId: g.guestHostId!, hypervisorId: g.hypervisorHostId!, virtualMachineId: g.virtualMachineId, cpuCores: g.cpuCores, memoryMiB: g.memoryMiB })),
        )
        updated.forEach(onApplied)
      }
      const newGuests = addable.filter((g) => chosenNew.has(g.virtualMachineId))
      if (newGuests.length > 0) {
        const created = await hostsApi.createFromScan(
          newGuests.map((g) => ({ ...g.newHost!, hypervisorId: g.hypervisorHostId!, virtualMachineId: g.virtualMachineId, cpuCores: g.cpuCores, memoryMiB: g.memoryMiB })),
        )
        created.forEach(onApplied)
      }
      onClose()
    } catch (failure) {
      const errors = toFormErrors(failure)
      setError([errors.message, ...Object.values(errors.fields)].filter(Boolean).join(' ') || 'The changes couldn’t be saved.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Dialog
      title={title}
      description={anything ? 'Choose what to apply. Nothing is saved until you do.' : undefined}
      size="lg"
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            {anything ? 'Cancel' : 'Close'}
          </Button>
          {anything ? (
            <Button onClick={() => void apply()} loading={saving} disabled={changeCount === 0}>
              {changeCount === 1 ? 'Apply 1 change' : `Apply ${changeCount} changes`}
            </Button>
          ) : null}
        </>
      }
    >
      <div className={styles.results}>
        {error ? <Alert>{error}</Alert> : null}
        {!anything ? <Alert variant="info">The scan found nothing that the host doesn’t already have.</Alert> : null}

        {GROUPS.map(({ group, title: heading }) => {
          const items = proposals.filter((p) => p.group === group)
          return items.length === 0 ? null : (
            <fieldset key={group} className={styles.group}>
              <legend className={styles.heading}>{heading}</legend>
              {items.map((p) => (
                <div key={p.key} className={styles.proposal}>
                  <Checkbox label={p.label} checked={chosen.has(p.key)} onChange={(event) => toggle(p.key, event.target.checked)} />
                  <span className={styles.detail}>{p.detail}</span>
                </div>
              ))}
            </fieldset>
          )
        })}

        {links.length > 0 ? (
          <fieldset className={styles.group}>
            <legend className={styles.heading}>Virtual machines</legend>
            {links.map((guest) => (
              <div key={guest.virtualMachineId} className={styles.proposal}>
                <Checkbox
                  label={`${guest.guestHostName} runs on ${guest.hypervisorHostName}`}
                  checked={chosenLinks.has(guest.virtualMachineId)}
                  onChange={(event) => toggleLink(guest.virtualMachineId, event.target.checked)}
                />
                <span className={styles.detail}>{guestDetail(guest)}</span>
              </div>
            ))}
          </fieldset>
        ) : null}

        {addable.length > 0 ? (
          <fieldset className={styles.group}>
            <legend className={styles.heading}>Add to NKeeper</legend>
            <p className={styles.muted}>
              Not in NKeeper yet. Each becomes a host linked to {addable[0].hypervisorHostName}
              {addable.some((g) => g.hypervisorHostId !== addable[0].hypervisorHostId) ? ' (or its node)' : ''}, with the address and MAC
              address the scan found. Add services and logins afterwards.
            </p>
            {addable.length > 1 ? (
              <div>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() =>
                    setChosenNew(chosenNew.size === addable.length ? new Set() : new Set(addable.map((g) => g.virtualMachineId)))
                  }
                >
                  {chosenNew.size === addable.length ? 'Select none' : `Select all ${addable.length}`}
                </Button>
              </div>
            ) : null}
            {addable.map((guest) => (
              <div key={guest.virtualMachineId} className={styles.proposal}>
                <Checkbox
                  label={`Add ${guest.newHost!.name}`}
                  checked={chosenNew.has(guest.virtualMachineId)}
                  onChange={(event) =>
                    setChosenNew((current) => {
                      const next = new Set(current)
                      if (event.target.checked) {
                        next.add(guest.virtualMachineId)
                      } else {
                        next.delete(guest.virtualMachineId)
                      }
                      return next
                    })
                  }
                />
                <span className={styles.detail}>{newHostDetail(guest)}</span>
              </div>
            ))}
          </fieldset>
        ) : null}

        {unlinked.length > 0 ? (
          <section className={styles.group} aria-label="Other virtual machines">
            <h3 className={styles.heading}>{links.length + addable.length > 0 ? 'Other virtual machines' : 'Virtual machines'}</h3>
            <ul className={styles.notes}>
              {unlinked.map((guest) => (
                <li key={guest.virtualMachineId}>
                  <strong>{guest.name}</strong>
                  {guest.node ? ` (on ${guest.node})` : ''}: {guest.note}
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        {otherPorts.length > 0 ? (
          <section className={styles.group} aria-label="Other open ports">
            <h3 className={styles.heading}>Other open ports</h3>
            <p className={styles.muted}>{otherPorts.map((p) => (p.name ? `${p.port} (${p.name})` : String(p.port))).join(', ')}</p>
          </section>
        ) : null}

        {findings.notes.length > 0 ? (
          <section className={styles.group} aria-label="Scan notes">
            <h3 className={styles.heading}>Notes</h3>
            <ul className={styles.notes}>
              {findings.notes.map((note, index) => (
                <li key={index}>{note}</li>
              ))}
            </ul>
          </section>
        ) : null}
      </div>
    </Dialog>
  )
}

const PLATFORMS: Record<ScannedGuest['platform'], string> = { libvirt: 'libvirt VM', 'proxmox-qemu': 'Proxmox VM', 'proxmox-lxc': 'Proxmox container' }
const MATCHED_BY = { id: 'its VM identity', mac: 'its MAC address', name: 'its name' } as const

function guestDetail(guest: ScannedGuest): string {
  return [
    `${PLATFORMS[guest.platform]} “${guest.name}”${guest.state ? ` (${guest.state})` : ''}`,
    guest.matchedBy ? `matched by ${MATCHED_BY[guest.matchedBy]}` : null,
    guestResources(guest),
    guest.note,
  ]
    .filter(Boolean)
    .join(' · ')
}

/** What the hypervisor gives a guest, e.g. "4 vCPUs, 8 GiB"; null when unknown. */
function guestResources(guest: ScannedGuest): string | null {
  const parts = [
    guest.cpuCores ? `${guest.cpuCores} vCPU${guest.cpuCores === 1 ? '' : 's'}` : null,
    guest.memoryMiB ? formatMemory(guest.memoryMiB) : null,
  ].filter(Boolean)
  return parts.length > 0 ? parts.join(', ') : null
}

/** What a new host would get: its address, MAC address, and where it runs. */
function newHostDetail(guest: ScannedGuest): string {
  const host = guest.newHost!
  return [
    `${PLATFORMS[guest.platform]} on ${guest.hypervisorHostName}`,
    host.ipAddress ?? `hostname ${host.hostname}`,
    host.macAddress,
    guestResources(guest),
  ]
    .filter(Boolean)
    .join(' · ')
}
