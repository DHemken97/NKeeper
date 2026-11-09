import { Fragment, useEffect, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Badge } from '../../../components/ui/Badge'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Dialog } from '../../../components/ui/Dialog'
import { Spinner } from '../../../components/ui/Spinner'
import { TextField } from '../../../components/ui/TextField'
import { ApiError } from '../../../lib/api'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi, networkScanApi } from '../api/inventoryApi'
import { OS_TAG } from '../scanProposals'
import { PROTOCOL_INFO, type DiscoveredDevice, type HostInput, type HostService, type LocalNetwork, type NetworkScan } from '../types'
import styles from './NetworkScanDialog.module.css'

interface NetworkScanDialogProps {
  /** The names hosts already have, so added machines get names of their own. */
  hostNames: readonly string[]
  onClose: () => void
  /** Called after hosts were added, so the host list can reload. */
  onAdded: () => void
}

const POLL_MS = 1500
const MATCHED_BY = { mac: 'MAC address', ip: 'IP address', hostname: 'hostname' } as const
const FOUND_BY: Record<string, string> = { ping: 'ping', tcp: 'TCP', arp: 'ARP' }

/**
 * Hosts ▸ Scan network: the server sweeps a whole subnet and learns what it can about every machine
 * that answers. It warns first, because a sweep is loud. Machines NKeeper doesn't have can be added
 * as hosts, with their names, addresses and the services found on them.
 */
export function NetworkScanDialog({ hostNames, onClose, onAdded }: NetworkScanDialogProps) {
  const [scan, setScan] = useState<NetworkScan | null>(null)
  const [loading, setLoading] = useState(true)
  const [networks, setNetworks] = useState<LocalNetwork[]>([])
  const [subnet, setSubnet] = useState('')
  const [acknowledged, setAcknowledged] = useState(false)
  const [subnetError, setSubnetError] = useState<string | undefined>()
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [chosen, setChosen] = useState<ReadonlySet<string>>(new Set())
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(new Set())
  /** Machines added in this dialog: their IP address, and the host's name. */
  const [added, setAdded] = useState<ReadonlyMap<string, string>>(new Map())
  const [addErrors, setAddErrors] = useState<string[]>([])

  // Pick up the user's last scan (it runs on the server, so it may still be going), and the server's networks.
  useEffect(() => {
    let cancelled = false
    void Promise.allSettled([networkScanApi.latest(), networkScanApi.networks()]).then(([latest, local]) => {
      if (cancelled) {
        return
      }
      const found = local.status === 'fulfilled' ? local.value : []
      setNetworks(found)
      setSubnet((current) => current || found[0]?.subnet || '')
      // A finished scan is only shown again when it's recent; otherwise start afresh.
      if (latest.status === 'fulfilled' && latest.value && (latest.value.state === 'running' || isRecent(latest.value))) {
        setScan(latest.value)
      }
      setLoading(false)
    })
    return () => {
      cancelled = true
    }
  }, [])

  const running = scan?.state === 'running'
  const scanId = scan?.id
  useEffect(() => {
    if (!running || !scanId) {
      return
    }
    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      networkScanApi
        .get(scanId, controller.signal)
        .then(setScan)
        .catch((caught: unknown) => {
          if (!controller.signal.aborted) {
            setError(caught instanceof ApiError ? caught.message : 'The scan’s progress couldn’t be read.')
          }
        })
    }, POLL_MS)
    return () => {
      window.clearTimeout(timer)
      controller.abort()
    }
  }, [running, scanId, scan])

  async function start() {
    setBusy(true)
    setError(null)
    setSubnetError(undefined)
    try {
      setChosen(new Set())
      setExpanded(new Set())
      setAdded(new Map())
      setAddErrors([])
      setScan(await networkScanApi.start(subnet.trim()))
    } catch (caught) {
      const errors = toFormErrors(caught, ['subnet', 'acknowledged'])
      setSubnetError(errors.fields.subnet)
      setError(errors.fields.acknowledged ?? errors.message ?? (caught instanceof ApiError && caught.status === 409 ? caught.message : null))
    } finally {
      setBusy(false)
    }
  }

  async function cancel() {
    if (!scan) {
      return
    }
    setBusy(true)
    try {
      await networkScanApi.cancel(scan.id)
      setScan(await networkScanApi.get(scan.id))
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'The scan couldn’t be cancelled.')
    } finally {
      setBusy(false)
    }
  }

  async function addChosen() {
    if (!scan) {
      return
    }
    setBusy(true)
    const errors: string[] = []
    const nowAdded = new Map(added)
    const taken = new Set([...hostNames, ...nowAdded.values()].map((name) => name.toLowerCase()))
    for (const device of scan.devices.filter((d) => chosen.has(d.ipAddress) && d.newHost && !nowAdded.has(d.ipAddress))) {
      const input = deviceToInput(device, taken)
      try {
        const host = await hostsApi.create(input)
        nowAdded.set(device.ipAddress, host.name)
        taken.add(host.name.toLowerCase())
      } catch (caught) {
        const problem = toFormErrors(caught)
        errors.push(`${device.ipAddress}: ${Object.values(problem.fields)[0] ?? problem.message ?? 'it couldn’t be added.'}`)
      }
    }
    setAdded(nowAdded)
    setAddErrors(errors)
    setChosen(new Set())
    setBusy(false)
    if (nowAdded.size > added.size) {
      onAdded()
    }
  }

  function startOver() {
    setScan(null)
    setAcknowledged(false)
    setError(null)
  }

  if (loading) {
    return (
      <Dialog title="Scan network" onClose={onClose}>
        <Spinner />
      </Dialog>
    )
  }

  if (!scan) {
    return (
      <Dialog
        title="Scan network"
        description="Find every machine on a subnet, and what it is."
        size="lg"
        onClose={onClose}
        footer={
          <>
            <Button variant="secondary" onClick={onClose} disabled={busy}>
              Cancel
            </Button>
            <Button variant="danger" onClick={() => void start()} loading={busy} disabled={!acknowledged || subnet.trim() === ''}>
              Start scan
            </Button>
          </>
        }
      >
        <form
          className={styles.form}
          onSubmit={(event) => {
            event.preventDefault()
            if (acknowledged && subnet.trim()) {
              void start()
            }
          }}
        >
          <Alert variant="warning">
            <strong>This scan is loud.</strong> The NKeeper server pings every address in the subnet, tries dozens of TCP ports on each, and
            talks to the services it finds (SSH, web, Remote Desktop, NetBIOS). Firewalls, intrusion detection and prevention systems,
            honeypots and other security tools may see it as an attack: they may raise alerts, block the NKeeper server, or wake up your
            security team. Only scan networks you’re allowed to, and consider telling whoever watches them first.
          </Alert>
          {error ? <Alert>{error}</Alert> : null}
          <TextField
            label="Subnet"
            placeholder="192.168.1.0/24"
            value={subnet}
            onChange={(event) => {
              setSubnet(event.target.value)
              setSubnetError(undefined)
            }}
            error={subnetError}
            hint="An IPv4 network in CIDR form, a /20 (4,096 addresses) at most. A /24 takes about a minute."
            autoComplete="off"
            spellCheck={false}
          />
          {networks.length > 0 ? (
            <div className={styles.suggestions}>
              <span className={styles.hint}>The server’s networks:</span>
              {networks.map((network) => (
                <button
                  key={network.subnet}
                  type="button"
                  className={styles.suggestion}
                  onClick={() => setSubnet(network.subnet)}
                  title={`${network.interface}, where the server is ${network.address}`}
                >
                  {network.subnet}
                </button>
              ))}
            </div>
          ) : null}
          <Checkbox
            label="I understand this scan is loud and may set off firewalls, honeypots or security monitoring, and I’m allowed to scan this network."
            checked={acknowledged}
            onChange={(event) => setAcknowledged(event.target.checked)}
          />
        </form>
      </Dialog>
    )
  }

  const addable = scan.devices.filter((d) => d.newHost && !added.has(d.ipAddress))
  const percent = scan.total > 0 ? Math.round((scan.done / scan.total) * 100) : 100
  return (
    <Dialog
      title={`Network scan of ${scan.subnet}`}
      description={summary(scan)}
      size="lg"
      onClose={onClose}
      footer={
        <>
          {running ? (
            <Button variant="secondary" onClick={() => void cancel()} loading={busy}>
              Stop scan
            </Button>
          ) : (
            <Button variant="secondary" onClick={startOver} disabled={busy}>
              New scan
            </Button>
          )}
          <Button variant="secondary" onClick={onClose} disabled={busy && !running}>
            Close
          </Button>
          {addable.length > 0 ? (
            <Button onClick={() => void addChosen()} loading={busy && !running} disabled={chosen.size === 0}>
              {chosen.size === 0 ? 'Add hosts' : `Add ${chosen.size} host${chosen.size === 1 ? '' : 's'}`}
            </Button>
          ) : null}
        </>
      }
    >
      <div className={styles.form}>
        {running || scan.state !== 'finished' ? (
          <div className={styles.progress}>
            <progress max={scan.total} value={scan.done} aria-label="Addresses scanned" />
            <span>
              {running ? `${percent}%` : null}
              {scan.state === 'cancelled' ? 'Stopped. These are the machines found before it stopped.' : null}
            </span>
          </div>
        ) : null}
        {scan.message ? <Alert>{scan.message}</Alert> : null}
        {error ? <Alert>{error}</Alert> : null}
        {addErrors.length > 0 ? (
          <Alert>
            Some machines couldn’t be added:
            <ul className={styles.list}>
              {addErrors.map((message) => (
                <li key={message}>{message}</li>
              ))}
            </ul>
          </Alert>
        ) : null}
        {added.size > 0 ? <Alert variant="info">Added {[...added.values()].join(', ')}.</Alert> : null}

        {scan.devices.length === 0 ? (
          <p className={styles.empty}>{running ? 'Nothing found yet…' : 'No machines answered.'}</p>
        ) : (
          <>
            {addable.length > 1 ? (
              <button
                type="button"
                className={styles.selectAll}
                onClick={() => setChosen(chosen.size === addable.length ? new Set() : new Set(addable.map((d) => d.ipAddress)))}
              >
                {chosen.size === addable.length ? 'Select none' : `Select all ${addable.length} new machines`}
              </button>
            ) : null}
            <div className={styles.tableWrap}>
              <table className={styles.table}>
                <thead>
                  <tr>
                    <th scope="col">
                      <span className="visually-hidden">Add</span>
                    </th>
                    <th scope="col">Address</th>
                    <th scope="col">Name</th>
                    <th scope="col">MAC address</th>
                    <th scope="col">System</th>
                    <th scope="col">Open ports</th>
                    <th scope="col">In NKeeper</th>
                  </tr>
                </thead>
                <tbody>
                  {scan.devices.map((device) => {
                    const open = expanded.has(device.ipAddress)
                    const addedAs = added.get(device.ipAddress)
                    return (
                      <Fragment key={device.ipAddress}>
                        <tr>
                          <td>
                            {device.newHost && !addedAs ? (
                              <input
                                type="checkbox"
                                className={styles.check}
                                aria-label={`Add ${device.ipAddress}`}
                                checked={chosen.has(device.ipAddress)}
                                onChange={(event) =>
                                  setChosen((current) => {
                                    const next = new Set(current)
                                    if (event.target.checked) {
                                      next.add(device.ipAddress)
                                    } else {
                                      next.delete(device.ipAddress)
                                    }
                                    return next
                                  })
                                }
                              />
                            ) : null}
                          </td>
                          <td>
                            <button
                              type="button"
                              className={styles.expand}
                              aria-expanded={open}
                              title={open ? 'Hide details' : 'Show everything found'}
                              onClick={() =>
                                setExpanded((current) => {
                                  const next = new Set(current)
                                  if (open) {
                                    next.delete(device.ipAddress)
                                  } else {
                                    next.add(device.ipAddress)
                                  }
                                  return next
                                })
                              }
                            >
                              <span aria-hidden="true">{open ? '▾' : '▸'}</span> {device.ipAddress}
                            </button>
                          </td>
                          <td className={styles.name}>{device.hostname ?? <span className={styles.muted}>unknown</span>}</td>
                          <td>
                            {device.macAddress ? <span className={styles.mono}>{device.macAddress}</span> : <span className={styles.muted}>unknown</span>}
                            {device.vendor ? <div className={styles.sub}>{device.vendor}</div> : null}
                          </td>
                          <td>{device.operatingSystem ?? (device.isVirtual ? 'Virtual machine' : <span className={styles.muted}>unknown</span>)}</td>
                          <td>
                            <div className={styles.ports}>
                              {device.ports.length === 0 ? <span className={styles.muted}>none</span> : null}
                              {device.ports.map((port) => (
                                <Badge key={port.port} variant={port.protocol ? 'accent' : 'neutral'} title={port.name ?? undefined}>
                                  {port.port}
                                </Badge>
                              ))}
                            </div>
                          </td>
                          <td>
                            {addedAs ? (
                              <>Added as {addedAs}</>
                            ) : device.matchedHostName ? (
                              <>
                                {device.matchedHostName}
                                {device.matchedBy ? <div className={styles.sub}>by {MATCHED_BY[device.matchedBy]}</div> : null}
                              </>
                            ) : (
                              <span className={styles.muted}>New</span>
                            )}
                          </td>
                        </tr>
                        {open ? (
                          <tr className={styles.detailRow}>
                            <td />
                            <td colSpan={6}>
                              <DeviceDetails device={device} />
                            </td>
                          </tr>
                        ) : null}
                      </Fragment>
                    )
                  })}
                </tbody>
              </table>
            </div>
          </>
        )}

        {scan.notes.length > 0 ? (
          <section>
            <h3 className={styles.heading}>Notes</h3>
            <ul className={styles.list}>
              {scan.notes.map((note) => (
                <li key={note}>{note}</li>
              ))}
            </ul>
          </section>
        ) : null}
      </div>
    </Dialog>
  )
}

/** Everything the scan learned about one machine. */
function DeviceDetails({ device }: { device: DiscoveredDevice }) {
  return (
    <dl className={styles.details}>
      <dt>Found by</dt>
      <dd>
        {device.respondedTo.map((how) => FOUND_BY[how] ?? how).join(', ')}
        {device.ttl ? ` (ping TTL ${device.ttl})` : ''}
      </dd>
      {device.names.length > 0 ? (
        <>
          <dt>Names</dt>
          <dd>{device.names.join(', ')}</dd>
        </>
      ) : null}
      {device.ports.length > 0 ? (
        <>
          <dt>Ports</dt>
          <dd>{device.ports.map((p) => (p.name ? `${p.port} ${p.name}` : String(p.port))).join(', ')}</dd>
        </>
      ) : null}
      {device.details.length > 0 ? (
        <>
          <dt>Said</dt>
          <dd>
            <ul className={styles.list}>
              {device.details.map((detail) => (
                <li key={detail}>{detail}</li>
              ))}
            </ul>
          </dd>
        </>
      ) : null}
      {device.newHost ? (
        <>
          <dt>Would add</dt>
          <dd>
            {device.newHost.name}
            {servicesOf(device).length > 0 ? ` with ${servicesOf(device).map((s) => PROTOCOL_INFO[s.protocol].label).join(', ')}` : ''}
            {device.operatingSystem ? ` and the tag ${OS_TAG}: ${device.operatingSystem}` : ''}
          </dd>
        </>
      ) : null}
    </dl>
  )
}

function summary(scan: NetworkScan): string {
  const found = `${scan.devices.length} machine${scan.devices.length === 1 ? '' : 's'} found`
  const known = scan.devices.filter((d) => d.hostId).length
  const progress = scan.state === 'running' ? `Scanned ${scan.done} of ${scan.total} addresses · ` : ''
  return `${progress}${found}${known > 0 ? `, ${known} already in NKeeper` : ''}.`
}

/** A finished scan is offered again for an hour, so closing the dialog doesn't lose it. */
function isRecent(scan: NetworkScan): boolean {
  return scan.finishedAtUtc !== null && Date.now() - Date.parse(scan.finishedAtUtc) < 60 * 60 * 1000
}

/** The services a machine's open ports become: one per protocol and port, the default port left blank. */
function servicesOf(device: DiscoveredDevice): HostService[] {
  const services: HostService[] = []
  for (const port of device.ports) {
    if (!port.protocol) {
      continue
    }
    const stored = port.port === PROTOCOL_INFO[port.protocol].defaultPort ? null : port.port
    if (!services.some((s) => s.protocol === port.protocol && s.port === stored)) {
      services.push({ protocol: port.protocol, port: stored, label: null })
    }
  }
  return services
}

/**
 * The host to add for a machine. Its name is the proposed one, or its IP address when a host
 * already has that name. `taken` holds the names in use, lower-cased.
 */
function deviceToInput(device: DiscoveredDevice, taken: ReadonlySet<string>): HostInput {
  const proposed = device.newHost!
  const name = taken.has(proposed.name.toLowerCase()) ? device.ipAddress : proposed.name
  return {
    name,
    hostname: proposed.hostname,
    ipAddress: proposed.ipAddress,
    macAddress: proposed.macAddress,
    description: proposed.description,
    isVirtualMachine: device.isVirtual,
    hypervisorId: null,
    cpuCores: null,
    memoryMiB: null,
    serialNumber: null,
    jumpHostId: null,
    jumpCredentialId: null,
    zabbixHostId: null,
    tags: device.operatingSystem ? [{ name: OS_TAG, value: device.operatingSystem }] : [],
    services: servicesOf(device),
    credentials: [],
  }
}
