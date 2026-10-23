import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Dialog } from '../../../components/ui/Dialog'
import { PasswordField } from '../../../components/ui/PasswordField'
import { Spinner } from '../../../components/ui/Spinner'
import { TextField } from '../../../components/ui/TextField'
import { ApiError } from '../../../lib/api'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../api/inventoryApi'
import { toHostDraft, toHostInput } from '../drafts'
import { credentialNeedsPassword, credentialProblem } from '../hostMenu'
import { scanProposals, type ScanProposal } from '../scanProposals'
import { PROTOCOL_INFO, type Host, type InternalScanLogin, type ScanFindings, type ScannedGuest } from '../types'
import styles from './ScanDialog.module.css'

interface BulkScanDialogProps {
  /** The hosts to scan (ones the user can manage). */
  hosts: Host[]
  /** Administrators can add hosts for VMs that aren't in NKeeper yet. */
  canAddHosts: boolean
  onClose: () => void
  /** Each host the applied findings changed or added. */
  onApplied: (host: Host) => void
}

/** Hosts scanned at the same time. */
const PARALLEL = 4

interface Item {
  host: Host
  login: InternalScanLogin | null
  sshPort: number | null
  status: 'waiting' | 'scanning' | 'done' | 'failed' | 'skipped'
  message?: string
  findings?: ScanFindings
}

/** The host's first SSH service, and its first saved login that needs no typing. */
function savedLogin(host: Host): { login: InternalScanLogin | null; sshPort: number | null; skip?: string } {
  const serviceIndex = host.services.findIndex((s) => s.protocol === 'Ssh')
  if (serviceIndex === -1) {
    return { login: null, sshPort: null, skip: 'No SSH service' }
  }
  const sshPort = host.services[serviceIndex].port ?? PROTOCOL_INFO.Ssh.defaultPort ?? 22
  const credential = host.credentials.find((c) => credentialProblem(c, 'Ssh') === null && !credentialNeedsPassword(c, 'Ssh'))
  return credential
    ? { login: { serviceIndex, credentialId: credential.id }, sshPort }
    : { login: null, sshPort, skip: 'No saved SSH login with a password or key' }
}

/**
 * Internal scans of several hosts (from the bulk panel): each logs in over SSH with its own saved
 * login, or one login for all, a few at a time. The findings are then reviewed together and only
 * the chosen ones are saved.
 */
export function BulkScanDialog({ hosts, canAddHosts, onClose, onApplied }: BulkScanDialogProps) {
  const [mode, setMode] = useState<'saved' | 'oneOff'>('saved')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [trustNewKeys, setTrustNewKeys] = useState(false)
  const [items, setItems] = useState<Item[] | null>(null)
  const [setupError, setSetupError] = useState<string | null>(null)
  const cancelled = useRef(false)

  useEffect(() => {
    cancelled.current = false
    return () => {
      cancelled.current = true
    }
  }, [])

  const readyCount = useMemo(() => hosts.filter((h) => (mode === 'saved' ? savedLogin(h).login : h.services.some((s) => s.protocol === 'Ssh'))).length, [hosts, mode])

  async function start() {
    if (mode === 'oneOff' && !username.trim()) {
      setSetupError('Enter the username to log in with.')
      return
    }
    const initial: Item[] = hosts.map((host) => {
      const saved = savedLogin(host)
      if (mode === 'saved') {
        return { host, login: saved.login, sshPort: saved.sshPort, status: saved.login ? 'waiting' : 'skipped', message: saved.skip }
      }
      const serviceIndex = host.services.findIndex((s) => s.protocol === 'Ssh')
      return serviceIndex === -1
        ? { host, login: null, sshPort: null, status: 'skipped', message: 'No SSH service' }
        : { host, login: { serviceIndex, username: username.trim(), password }, sshPort: saved.sshPort, status: 'waiting' }
    })
    setItems(initial)

    const update = (id: string, patch: Partial<Item>) =>
      !cancelled.current && setItems((current) => current?.map((item) => (item.host.id === id ? { ...item, ...patch } : item)) ?? current)
    const queue = initial.filter((item) => item.status === 'waiting')
    const scanOne = async (item: Item) => {
      update(item.host.id, { status: 'scanning' })
      try {
        let response = await hostsApi.scanInternal(item.host.id, item.login!)
        if (response.status === 'trustRequired' && response.hostKey) {
          if (!trustNewKeys) {
            update(item.host.id, { status: 'skipped', message: 'Its SSH host key isn’t trusted yet: scan it on its own to check the key, or trust new keys.' })
            return
          }
          response = await hostsApi.scanInternal(item.host.id, item.login!, response.hostKey.fingerprint)
        }
        update(item.host.id, response.findings ? { status: 'done', findings: response.findings } : { status: 'failed', message: 'The scan returned nothing.' })
      } catch (error) {
        update(item.host.id, { status: 'failed', message: error instanceof ApiError ? error.message : 'The scan failed.' })
      }
    }
    const worker = async () => {
      for (let item = queue.shift(); item && !cancelled.current; item = queue.shift()) {
        await scanOne(item)
      }
    }
    await Promise.all(Array.from({ length: Math.min(PARALLEL, queue.length) }, worker))
  }

  const finished = items !== null && items.every((item) => item.status !== 'waiting' && item.status !== 'scanning')
  if (items && finished) {
    return <BulkScanReview items={items} canAddHosts={canAddHosts} onClose={onClose} onApplied={onApplied} />
  }

  const title = `Internal scan of ${hosts.length} hosts`
  if (items) {
    const done = items.filter((i) => i.status !== 'waiting' && i.status !== 'scanning').length
    return (
      <Dialog title={title} description={`${done} of ${items.length} finished…`} size="lg" onClose={onClose} footer={<Button variant="secondary" onClick={onClose}>Stop</Button>}>
        <ScanProgress items={items} />
      </Dialog>
    )
  }

  return (
    <Dialog
      title={title}
      description="Each host gets one SSH login that reads its own settings. Nothing is saved until you review the results."
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => void start()} disabled={readyCount === 0}>
            {readyCount === 1 ? 'Scan 1 host' : `Scan ${readyCount} hosts`}
          </Button>
        </>
      }
    >
      <div className={styles.text}>
        {setupError ? <Alert>{setupError}</Alert> : null}
        <fieldset className={styles.group}>
          <legend className={styles.heading}>Log in with</legend>
          <label className={styles.choice}>
            <input type="radio" name="bulk-scan-login" checked={mode === 'saved'} onChange={() => setMode('saved')} />
            Each host’s saved SSH login (hosts without one are skipped)
          </label>
          <label className={styles.choice}>
            <input type="radio" name="bulk-scan-login" checked={mode === 'oneOff'} onChange={() => setMode('oneOff')} />
            One login for all of them (used once, not saved)
          </label>
        </fieldset>
        {mode === 'oneOff' ? (
          <>
            <TextField label="Username" value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" spellCheck={false} />
            <PasswordField label="Password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" />
          </>
        ) : null}
        <Checkbox label="Trust new SSH host keys (first connection)" checked={trustNewKeys} onChange={(e) => setTrustNewKeys(e.target.checked)} />
        <p className={styles.muted}>
          Without it, hosts NKeeper hasn’t connected to before are skipped, so you can check their keys one at a time. Changed keys are always
          refused.
        </p>
      </div>
    </Dialog>
  )
}

const STATUS: Record<Item['status'], string> = { waiting: 'Waiting', scanning: 'Scanning…', done: 'Done', failed: 'Failed', skipped: 'Skipped' }

function ScanProgress({ items }: { items: Item[] }) {
  return (
    <ul className={styles.progress}>
      {items.map((item) => (
        <li key={item.host.id}>
          {item.status === 'scanning' ? <Spinner size="sm" /> : null}
          <strong>{item.host.name}</strong> <span className={styles.muted}>{STATUS[item.status]}</span>
          {item.message ? <span className={styles.detail}> · {item.message}</span> : null}
        </li>
      ))}
    </ul>
  )
}

interface ReviewProps {
  items: Item[]
  canAddHosts: boolean
  onClose: () => void
  onApplied: (host: Host) => void
}

/** The first of each VM (by its identity): two nodes of a Proxmox cluster report the same guests. */
function uniqueGuests(guests: ScannedGuest[]): ScannedGuest[] {
  const seen = new Set<string>()
  return guests.filter((g) => !seen.has(g.virtualMachineId) && seen.add(g.virtualMachineId))
}

function BulkScanReview({ items, canAddHosts, onClose, onApplied }: ReviewProps) {
  const scanned = useMemo(() => items.filter((i) => i.status === 'done' && i.findings), [items])
  const problems = useMemo(() => items.filter((i) => i.status === 'failed' || i.status === 'skipped'), [items])
  const perHost = useMemo(
    () => scanned.map((item) => ({ item, proposals: scanProposals(item.host, item.findings!, item.sshPort) })).filter((h) => h.proposals.length > 0),
    [scanned],
  )
  const guests = useMemo(() => scanned.flatMap((i) => i.findings!.guests), [scanned])
  const links = useMemo(() => uniqueGuests(guests.filter((g) => g.linkProposed && g.guestHostId && g.hypervisorHostId)), [guests])
  const addable = useMemo(
    () => (canAddHosts ? uniqueGuests(guests.filter((g) => g.newHost && g.hypervisorHostId)) : []),
    [guests, canAddHosts],
  )
  // Two scanned hypervisors can each have a VM matching one host (every site's "zproxy"): let the user pick.
  const contested = useMemo(() => {
    const claims = new Map<string, number>()
    links.forEach((g) => claims.set(g.guestHostId!, (claims.get(g.guestHostId!) ?? 0) + 1))
    return new Set([...claims].filter(([, count]) => count > 1).map(([id]) => id))
  }, [links])

  const [chosen, setChosen] = useState<ReadonlySet<string>>(
    () =>
      new Set([
        ...perHost.flatMap(({ item, proposals }) => proposals.filter((p) => p.checked).map((p) => `${item.host.id}:${p.key}`)),
        ...links.filter((g) => !contested.has(g.guestHostId!)).map((g) => `link:${g.virtualMachineId}`),
      ]),
  )
  const [saving, setSaving] = useState(false)
  const [errors, setErrors] = useState<string[]>([])

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

  /** A "Select all / none" button for a list of guests, keyed by prefix. */
  function selectAll(prefix: string, list: ScannedGuest[]) {
    const keys = list.map((g) => `${prefix}:${g.virtualMachineId}`)
    const all = keys.every((k) => chosen.has(k))
    return list.length > 1 ? (
      <div>
        <Button
          variant="ghost"
          size="sm"
          onClick={() =>
            setChosen((current) => {
              const next = new Set(current)
              keys.forEach((k) => (all ? next.delete(k) : next.add(k)))
              return next
            })
          }
        >
          {all ? 'Select none' : `Select all ${list.length}`}
        </Button>
      </div>
    ) : null
  }

  async function apply() {
    setSaving(true)
    const failures: string[] = []
    const message = (error: unknown) => {
      const e = toFormErrors(error)
      return [e.message, ...Object.values(e.fields)].filter(Boolean).join(' ') || 'it couldn’t be saved'
    }
    for (const { item, proposals } of perHost) {
      const picked: ScanProposal[] = proposals.filter((p) => chosen.has(`${item.host.id}:${p.key}`))
      if (picked.length === 0) {
        continue
      }
      const draft = toHostDraft(item.host)
      picked.forEach((p) => p.apply(draft))
      try {
        onApplied(await hostsApi.update(item.host.id, toHostInput(draft, item.host.version)))
      } catch (error) {
        failures.push(`${item.host.name}: ${message(error)}`)
      }
    }
    const pickedLinks = links.filter((g) => chosen.has(`link:${g.virtualMachineId}`))
    if (pickedLinks.length > 0) {
      try {
        const updated = await hostsApi.linkVirtualMachines(
          pickedLinks.map((g) => ({ guestId: g.guestHostId!, hypervisorId: g.hypervisorHostId!, virtualMachineId: g.virtualMachineId, cpuCores: g.cpuCores, memoryMiB: g.memoryMiB })),
        )
        updated.forEach(onApplied)
      } catch (error) {
        failures.push(`Virtual machine links: ${message(error)}`)
      }
    }
    const pickedNew = addable.filter((g) => chosen.has(`new:${g.virtualMachineId}`))
    if (pickedNew.length > 0) {
      try {
        const created = await hostsApi.createFromScan(
          pickedNew.map((g) => ({ ...g.newHost!, hypervisorId: g.hypervisorHostId!, virtualMachineId: g.virtualMachineId, cpuCores: g.cpuCores, memoryMiB: g.memoryMiB })),
        )
        created.forEach(onApplied)
      } catch (error) {
        failures.push(`New hosts: ${message(error)}`)
      }
    }
    setSaving(false)
    setErrors(failures)
    if (failures.length === 0) {
      onClose()
    }
  }

  const anything = perHost.length + links.length + addable.length > 0
  return (
    <Dialog
      title={`Scanned ${scanned.length} of ${items.length} hosts`}
      description={anything ? 'Choose what to apply. Nothing is saved until you do.' : undefined}
      size="lg"
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            {anything ? 'Cancel' : 'Close'}
          </Button>
          {anything ? (
            <Button onClick={() => void apply()} loading={saving} disabled={chosen.size === 0}>
              {chosen.size === 1 ? 'Apply 1 change' : `Apply ${chosen.size} changes`}
            </Button>
          ) : null}
        </>
      }
    >
      <div className={styles.results}>
        {errors.length > 0 ? (
          <Alert>
            Some changes weren’t saved: {errors.join(' ')}
          </Alert>
        ) : null}
        {!anything && scanned.length > 0 ? <Alert variant="info">The scans found nothing that the hosts don’t already have.</Alert> : null}

        {problems.length > 0 ? (
          <section className={styles.group} aria-label="Not scanned">
            <h3 className={styles.heading}>Not scanned</h3>
            <ul className={styles.notes}>
              {problems.map((item) => (
                <li key={item.host.id}>
                  <strong>{item.host.name}</strong>: {item.message}
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        {perHost.map(({ item, proposals }) => (
          <fieldset key={item.host.id} className={styles.group}>
            <legend className={styles.heading}>{item.host.name}</legend>
            {proposals.map((p) => (
              <div key={p.key} className={styles.proposal}>
                <Checkbox
                  label={p.label}
                  checked={chosen.has(`${item.host.id}:${p.key}`)}
                  onChange={(event) => toggle(`${item.host.id}:${p.key}`, event.target.checked)}
                />
                <span className={styles.detail}>{p.detail}</span>
              </div>
            ))}
          </fieldset>
        ))}

        {links.length > 0 ? (
          <fieldset className={styles.group}>
            <legend className={styles.heading}>Virtual machines</legend>
            {selectAll('link', links)}
            {links.map((g) => (
              <div key={g.virtualMachineId} className={styles.proposal}>
                <Checkbox
                  label={`${g.guestHostName} runs on ${g.hypervisorHostName}`}
                  checked={chosen.has(`link:${g.virtualMachineId}`)}
                  onChange={(event) => toggle(`link:${g.virtualMachineId}`, event.target.checked)}
                />
                {contested.has(g.guestHostId!) ? (
                  <span className={styles.detail}>
                    VMs on more than one hypervisor match it, so none is ticked: pick the one it really is.
                  </span>
                ) : g.note ? (
                  <span className={styles.detail}>{g.note}</span>
                ) : null}
              </div>
            ))}
          </fieldset>
        ) : null}

        {addable.length > 0 ? (
          <fieldset className={styles.group}>
            <legend className={styles.heading}>Add to NKeeper</legend>
            {selectAll('new', addable)}
            {addable.map((g) => (
              <div key={g.virtualMachineId} className={styles.proposal}>
                <Checkbox
                  label={`Add ${g.newHost!.name}`}
                  checked={chosen.has(`new:${g.virtualMachineId}`)}
                  onChange={(event) => toggle(`new:${g.virtualMachineId}`, event.target.checked)}
                />
                <span className={styles.detail}>
                  on {g.hypervisorHostName} · {g.newHost!.ipAddress ?? `hostname ${g.newHost!.hostname}`}
                  {g.namesake ? ` · ${g.namesake}` : ''}
                </span>
              </div>
            ))}
          </fieldset>
        ) : null}
      </div>
    </Dialog>
  )
}
