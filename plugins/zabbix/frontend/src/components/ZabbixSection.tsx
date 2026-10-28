import { Fragment, useState } from 'react'
import {
  Alert,
  ApiError,
  Badge,
  Button,
  Checkbox,
  formatMemory,
  hostsApi,
  Spinner,
  styles as nkeeper,
  toFormErrors,
  toHostDraft,
  toHostInput,
  useResource,
  type Host,
} from '@nkeeper/plugin-sdk'
import { formatItemValue, INVENTORY_LABELS, zabbixHardware, type ZabbixHardware } from '../format'
import { linkedZabbixHost, SEVERITIES, zabbixApi, zabbixLinks, type ZabbixAvailability, type ZabbixHostOverview } from '../zabbixApi'
import styles from './Zabbix.module.css'

const panel = nkeeper.hostPanel

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

interface ZabbixSectionProps {
  /** A host linked to Zabbix (with the plugin's hostId). */
  host: Host
  /** The host changed here (details copied from Zabbix). */
  onChanged: (host: Host) => void
}

/** What Zabbix knows about a linked host: availability, problems, latest values, inventory and interfaces. */
export function ZabbixSection({ host, onChanged }: ZabbixSectionProps) {
  const overview = useResource((signal) => zabbixApi.overview(host.id, signal), `${host.id}:${linkedZabbixHost(host)}`)
  const status = useResource((signal) => zabbixApi.status(signal))
  const frontendUrl = status.data?.frontendUrl ?? null
  const zabbixHostId = linkedZabbixHost(host)!

  return (
    <section className={panel.section} aria-labelledby="zabbix-section-title">
      <div className={panel.sectionHeader}>
        <h2 id="zabbix-section-title" className={panel.sectionTitle}>
          Zabbix
        </h2>
        <div className={panel.badges}>
          {frontendUrl ? (
            <a className={panel.link} href={zabbixLinks.latestData(frontendUrl, zabbixHostId)} target="_blank" rel="noreferrer noopener">
              Open in Zabbix
            </a>
          ) : null}
          <Button variant="ghost" size="sm" onClick={overview.reload} loading={overview.loading && overview.data !== undefined}>
            Refresh
          </Button>
        </div>
      </div>
      {overview.error ? <Alert variant={isQuiet(overview.error) ? 'info' : 'error'}>{describeError(overview.error, zabbixHostId)}</Alert> : null}
      {!overview.data && !overview.error ? <Spinner label="Loading from Zabbix" /> : null}
      {overview.data ? <Overview host={host} overview={overview.data} frontendUrl={frontendUrl} onChanged={onChanged} /> : null}
    </section>
  )
}

function isQuiet(error: unknown): boolean {
  return error instanceof ApiError && (error.code === 'zabbix_disabled' || error.code === 'zabbix_host_not_found')
}

function describeError(error: unknown, zabbixHostId: string): string {
  if (error instanceof ApiError && error.code === 'zabbix_disabled') {
    return `Linked to Zabbix host ${zabbixHostId}, but Zabbix isn’t set up. An administrator can set it up under Settings.`
  }
  return toFormErrors(error).message ?? 'Zabbix couldn’t be reached.'
}

function Overview({
  host,
  overview,
  frontendUrl,
  onChanged,
}: {
  host: Host
  overview: ZabbixHostOverview
  frontendUrl: string | null
  onChanged: (host: Host) => void
}) {
  const zabbix = overview.host
  const inventory = Object.entries(overview.inventory)
  return (
    <>
      <dl className={panel.properties}>
        <dt>Zabbix host</dt>
        <dd>
          {zabbix.name}
          {zabbix.name !== zabbix.host ? <span className={panel.muted}> ({zabbix.host})</span> : null}
          <span className={`${panel.mono} ${panel.muted}`}> · ID {zabbix.hostId}</span>
        </dd>
        <dt>Status</dt>
        <dd>
          <Availability value={overview.availability} />
          {!zabbix.monitored ? <span className={panel.muted}> · Not monitored</span> : null}
          {overview.inMaintenance ? <span className={panel.muted}> · In maintenance</span> : null}
        </dd>
        {overview.groups.length > 0 ? (
          <>
            <dt>Host groups</dt>
            <dd>{overview.groups.join(', ')}</dd>
          </>
        ) : null}
        {zabbix.tags.length > 0 ? (
          <>
            <dt>Zabbix tags</dt>
            <dd>{zabbix.tags.map((t) => (t.value ? `${t.name}: ${t.value}` : t.name)).join(', ')}</dd>
          </>
        ) : null}
        {overview.description ? (
          <>
            <dt>Description</dt>
            <dd className={panel.description}>{overview.description}</dd>
          </>
        ) : null}
      </dl>

      {host.canManage ? <CopyToHost host={host} hardware={zabbixHardware(overview)} onChanged={onChanged} /> : null}

      <h3 className={panel.sectionTitle}>
        Problems <span className={panel.count}>{overview.problems.length}</span>
        {frontendUrl && overview.problems.length > 0 ? (
          <>
            {' '}
            <a className={panel.link} href={zabbixLinks.problems(frontendUrl, zabbix.hostId)} target="_blank" rel="noreferrer noopener">
              View in Zabbix
            </a>
          </>
        ) : null}
      </h3>
      {overview.problems.length === 0 ? (
        <p className={panel.muted}>No problems.</p>
      ) : (
        <ul className={panel.rows}>
          {overview.problems.map((problem) => (
            <li key={problem.eventId} className={panel.row}>
              <span className={`${styles.severity} ${styles[`severity${problem.severity}`] ?? ''}`}>{SEVERITIES[problem.severity] ?? 'Unknown'}</span>
              <span className={panel.rowMain}>{problem.name}</span>
              <span className={panel.muted}>
                since <time dateTime={problem.startedAtUtc}>{dateTimeFormat.format(new Date(problem.startedAtUtc))}</time>
                {problem.acknowledged ? ' · acknowledged' : ''}
                {problem.suppressed ? ' · suppressed' : ''}
              </span>
            </li>
          ))}
        </ul>
      )}

      {overview.items.length > 0 ? (
        <>
          <h3 className={panel.sectionTitle}>Latest values</h3>
          <dl className={panel.properties}>
            {overview.items.map((item) => (
              <Fragment key={item.key}>
                <dt title={item.key}>{item.name}</dt>
                <dd title={item.checkedAtUtc ? `Checked ${dateTimeFormat.format(new Date(item.checkedAtUtc))}` : undefined}>
                  {formatItemValue(item)}
                </dd>
              </Fragment>
            ))}
          </dl>
        </>
      ) : null}

      <h3 className={panel.sectionTitle}>Inventory</h3>
      {inventory.length === 0 ? (
        <p className={panel.muted}>No inventory. Turn on host inventory for this host in Zabbix (manual or automatic) to see it here.</p>
      ) : (
        <dl className={panel.properties}>
          {inventory.map(([field, value]) => (
            <Fragment key={field}>
              <dt title={field}>{INVENTORY_LABELS[field] ?? field}</dt>
              <dd className={panel.description}>{value}</dd>
            </Fragment>
          ))}
        </dl>
      )}

      {overview.interfaces.length > 0 ? (
        <>
          <h3 className={panel.sectionTitle}>Interfaces</h3>
          <ul className={panel.rows}>
            {overview.interfaces.map((item, index) => (
              <li key={index} className={panel.row}>
                <Badge variant={item.main ? 'accent' : 'neutral'}>{item.type}</Badge>
                <span className={`${panel.rowMain} ${panel.mono}`}>
                  {item.useIp ? item.ipAddress : item.dns}:{item.port}
                  {item.useIp && item.dns ? <span className={panel.muted}> ({item.dns})</span> : null}
                  {!item.useIp && item.ipAddress ? <span className={panel.muted}> ({item.ipAddress})</span> : null}
                </span>
                <Availability value={item.availability} />
                {item.error ? <span className={`${panel.status} ${panel.down}`}>{item.error}</span> : null}
              </li>
            ))}
          </ul>
        </>
      ) : null}
    </>
  )
}

function Availability({ value }: { value: ZabbixAvailability }) {
  const dot = { Available: styles.dotAvailable, Unavailable: styles.dotUnavailable, Mixed: styles.dotMixed, Unknown: '' }[value]
  const label = { Available: 'Available', Unavailable: 'Unavailable', Mixed: 'Partly available', Unknown: 'Availability unknown' }[value]
  return (
    <span className={styles.availability}>
      <span className={`${styles.dot} ${dot}`} aria-hidden="true" />
      {label}
    </span>
  )
}

interface Proposal {
  field: keyof ZabbixHardware
  label: string
  detail: string
}

/** Details Zabbix has that differ from the host's, to copy onto it (like a scan's findings). */
function CopyToHost({ host, hardware, onChanged }: { host: Host; hardware: ZabbixHardware; onChanged: (host: Host) => void }) {
  const now = (current: string | null) => (current ? ` (now ${current})` : '')
  const proposals: Proposal[] = []
  if (hardware.serialNumber && hardware.serialNumber !== host.serialNumber && !host.isVirtualMachine) {
    proposals.push({ field: 'serialNumber', label: 'Serial number', detail: hardware.serialNumber + now(host.serialNumber) })
  }
  if (hardware.macAddress && hardware.macAddress !== host.macAddress) {
    proposals.push({ field: 'macAddress', label: 'MAC address', detail: hardware.macAddress + now(host.macAddress) })
  }
  if (hardware.cpuCores && hardware.cpuCores !== host.cpuCores) {
    proposals.push({ field: 'cpuCores', label: host.isVirtualMachine ? 'vCPUs' : 'CPU cores', detail: String(hardware.cpuCores) + now(host.cpuCores ? String(host.cpuCores) : null) })
  }
  // Memory reported by the OS is a little under what's installed: only differences over 5% count.
  if (hardware.memoryMiB && (!host.memoryMiB || Math.abs(hardware.memoryMiB - host.memoryMiB) / host.memoryMiB > 0.05)) {
    proposals.push({ field: 'memoryMiB', label: 'Memory', detail: formatMemory(hardware.memoryMiB) + now(host.memoryMiB ? formatMemory(host.memoryMiB) : null) })
  }

  const [checked, setChecked] = useState<Set<string>>(() => new Set(proposals.filter((p) => !host[p.field]).map((p) => p.field)))
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  if (proposals.length === 0) {
    return null
  }

  async function apply() {
    setSaving(true)
    setError(null)
    try {
      const input = toHostInput(toHostDraft(host), host.version)
      for (const proposal of proposals) {
        if (checked.has(proposal.field)) {
          // Each field is copied as Zabbix has it.
          Object.assign(input, { [proposal.field]: hardware[proposal.field] })
        }
      }
      onChanged(await hostsApi.update(host.id, input))
    } catch (err) {
      setError(toFormErrors(err).message ?? 'The host couldn’t be saved.')
      setSaving(false)
    }
  }

  return (
    <div className={styles.apply} role="group" aria-label="Copy from Zabbix">
      <p>Zabbix has details this host doesn’t. Copy them to the host?</p>
      {proposals.map((proposal) => (
        <Checkbox
          key={proposal.field}
          label={`${proposal.label}: ${proposal.detail}`}
          checked={checked.has(proposal.field)}
          onChange={(e) =>
            setChecked((current) => {
              const next = new Set(current)
              if (e.target.checked) {
                next.add(proposal.field)
              } else {
                next.delete(proposal.field)
              }
              return next
            })
          }
        />
      ))}
      {error ? <p className={panel.fieldError}>{error}</p> : null}
      <div>
        <Button size="sm" onClick={() => void apply()} loading={saving} disabled={checked.size === 0}>
          Copy to host
        </Button>
      </div>
    </div>
  )
}
