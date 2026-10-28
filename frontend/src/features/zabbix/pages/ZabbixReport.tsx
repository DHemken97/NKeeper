import { useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Checkbox } from '../../../components/ui/Checkbox'
import { Spinner } from '../../../components/ui/Spinner'
import { ApiError } from '../../../lib/api'
import { toFormErrors } from '../../../lib/formErrors'
import { useResource } from '../../inventory/hooks/useResource'
import styles from '../../reports/pages/Reports.module.css'
import { zabbixApi, type ComparedHost, type MatchedBy, type ZabbixComparison, type ZabbixHostSummary } from '../zabbixApi'
import own from './ZabbixReport.module.css'

const MATCHED_BY: Record<MatchedBy, string> = { mac: 'MAC address', ip: 'IP address', hostname: 'Hostname', name: 'Name' }

/**
 * Zabbix next to NKeeper: probable matches to link, hosts only Zabbix has (to add to NKeeper),
 * hosts only NKeeper has, links to Zabbix hosts that are gone, and what's linked. Administrators.
 */
export function ZabbixReport() {
  const comparison = useResource((signal) => zabbixApi.comparison(signal))
  // Kept here: the comparison below remounts after each change.
  const [message, setMessage] = useState<Message | null>(null)

  if (comparison.error && !comparison.data) {
    const disabled = comparison.error instanceof ApiError && comparison.error.code === 'zabbix_disabled'
    return (
      <div className={styles.state}>
        <Alert variant={disabled ? 'info' : 'error'}>
          {disabled ? (
            <>
              Zabbix isn’t set up. Set it up under <Link to="/settings">Settings</Link> to compare it with NKeeper.
            </>
          ) : (
            toFormErrors(comparison.error).message
          )}
        </Alert>
      </div>
    )
  }

  if (!comparison.data) {
    return (
      <div className={styles.state}>
        <Spinner label="Comparing with Zabbix" />
      </div>
    )
  }

  // Remount after each change so selections start fresh.
  return (
    <Comparison
      key={comparisonKey(comparison.data)}
      data={comparison.data}
      loading={comparison.loading}
      reload={comparison.reload}
      message={message}
      setMessage={setMessage}
    />
  )
}

function comparisonKey(data: ZabbixComparison): string {
  return [data.linked.length, data.probableMatches.length, data.zabbixOnly.length, data.nKeeperOnly.length, data.brokenLinks.length].join('-')
}

type Message = { kind: 'info' | 'error'; text: ReactNode }

interface ComparisonProps {
  data: ZabbixComparison
  loading: boolean
  reload: () => void
  message: Message | null
  setMessage: (message: Message | null) => void
}

function Comparison({ data, loading, reload, message, setMessage }: ComparisonProps) {
  const [busy, setBusy] = useState<'link' | 'import' | 'unlink' | null>(null)
  const [toLink, setToLink] = useState(() => new Set(data.probableMatches.map((p) => p.host.id)))
  const [toAdd, setToAdd] = useState(() => new Set<string>())
  const [groupsAsTags, setGroupsAsTags] = useState(true)

  async function act(kind: 'link' | 'import' | 'unlink', action: () => Promise<ReactNode>) {
    setBusy(kind)
    setMessage(null)
    try {
      const text = await action()
      setMessage({ kind: 'info', text })
      reload()
    } catch (err) {
      setMessage({ kind: 'error', text: toFormErrors(err).message ?? 'That didn’t work.' })
    } finally {
      setBusy(null)
    }
  }

  const linkSelected = () =>
    act('link', async () => {
      const links = data.probableMatches.filter((p) => toLink.has(p.host.id)).map((p) => ({ hostId: p.host.id, zabbixHostId: p.zabbix.hostId }))
      await zabbixApi.link(links)
      return `Linked ${count(links.length, 'host')} to Zabbix.`
    })

  const addSelected = () =>
    act('import', async () => {
      const result = await zabbixApi.importHosts([...toAdd], groupsAsTags)
      return (
        <>
          Added {count(result.created.length, 'host')} to NKeeper.
          {result.skipped.length > 0 ? (
            <ul className={own.skipped}>
              {result.skipped.map((s) => (
                <li key={s.zabbixHostId}>
                  {s.name}: {s.reason}
                </li>
              ))}
            </ul>
          ) : null}
        </>
      )
    })

  const unlink = (hosts: ComparedHost[]) =>
    act('unlink', async () => {
      await zabbixApi.link(hosts.map((h) => ({ hostId: h.id, zabbixHostId: null })))
      return `Unlinked ${count(hosts.length, 'host')}.`
    })

  return (
    <>
      <header className={styles.reportHeader}>
        <div>
          <h2 className={styles.reportTitle}>Zabbix</h2>
          <p className={styles.reportSummary}>
            {data.linked.length} linked · {data.probableMatches.length} probable {data.probableMatches.length === 1 ? 'match' : 'matches'} ·{' '}
            {data.zabbixOnly.length} only in Zabbix · {data.nKeeperOnly.length} only in NKeeper
            {data.brokenLinks.length > 0 ? ` · ${data.brokenLinks.length} linked to a missing Zabbix host` : ''}
          </p>
        </div>
        <div className={styles.reportActions}>
          <Button size="sm" variant="secondary" onClick={reload} disabled={loading || busy !== null}>
            Refresh
          </Button>
        </div>
      </header>

      <div className={styles.tableScroll}>
        {message ? <Alert variant={message.kind}>{message.text}</Alert> : null}

        <section className={styles.healthSection} aria-labelledby="probable-title">
          <h3 id="probable-title" className={styles.healthTitle}>
            Probable matches
          </h3>
          {data.probableMatches.length === 0 ? (
            <p className={styles.healthEmpty}>No unlinked hosts look like a Zabbix host.</p>
          ) : (
            <>
              <p className={styles.healthEmpty}>
                These NKeeper hosts aren’t linked yet, but look like a Zabbix host: by MAC address, then IP address, then hostname, then
                name.
              </p>
              <table className={styles.healthTable}>
                <thead>
                  <tr>
                    <th scope="col">
                      <SelectAll ids={data.probableMatches.map((p) => p.host.id)} selected={toLink} onChange={setToLink} label="Select every match" />
                    </th>
                    <th scope="col">NKeeper host</th>
                    <th scope="col">Zabbix host</th>
                    <th scope="col">Matched by</th>
                  </tr>
                </thead>
                <tbody>
                  {data.probableMatches.map((pair) => (
                    <tr key={pair.host.id}>
                      <td>
                        <Toggle id={pair.host.id} selected={toLink} onChange={setToLink} label={`Link ${pair.host.name} to ${pair.zabbix.name}`} />
                      </td>
                      <td>
                        <HostLink host={pair.host} />
                        <div className={`${styles.mono} ${own.muted}`}>{addresses(pair.host)}</div>
                      </td>
                      <td>
                        <ZabbixName zabbix={pair.zabbix} />
                      </td>
                      <td>{pair.matchedBy ? MATCHED_BY[pair.matchedBy] : ''}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <div className={own.actions}>
                <Button size="sm" onClick={() => void linkSelected()} loading={busy === 'link'} disabled={busy !== null || toLink.size === 0}>
                  Link {count(toLink.size, 'host')}
                </Button>
              </div>
            </>
          )}
        </section>

        <section className={styles.healthSection} aria-labelledby="zabbix-only-title">
          <h3 id="zabbix-only-title" className={styles.healthTitle}>
            Only in Zabbix
          </h3>
          {data.zabbixOnly.length === 0 ? (
            <p className={styles.healthEmpty}>NKeeper has every host Zabbix has.</p>
          ) : (
            <>
              <table className={styles.healthTable}>
                <thead>
                  <tr>
                    <th scope="col">
                      <SelectAll ids={data.zabbixOnly.map((z) => z.hostId)} selected={toAdd} onChange={setToAdd} label="Select every Zabbix host" />
                    </th>
                    <th scope="col">Zabbix host</th>
                    <th scope="col">Addresses</th>
                    <th scope="col">OS</th>
                    <th scope="col">Tags</th>
                  </tr>
                </thead>
                <tbody>
                  {data.zabbixOnly.map((zabbix) => (
                    <tr key={zabbix.hostId}>
                      <td>
                        <Toggle id={zabbix.hostId} selected={toAdd} onChange={setToAdd} label={`Add ${zabbix.name}`} />
                      </td>
                      <td>
                        <ZabbixName zabbix={zabbix} />
                      </td>
                      <td className={styles.mono}>{[...zabbix.ipAddresses, ...zabbix.dnsNames, ...zabbix.macAddresses].join(', ') || '—'}</td>
                      <td>{zabbix.operatingSystem ?? ''}</td>
                      <td>{zabbix.tags.map((t) => (t.value ? `${t.name}: ${t.value}` : t.name)).join(', ')}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <div className={own.actions}>
                <Checkbox label="Add Zabbix host groups as “group” tags" checked={groupsAsTags} onChange={(e) => setGroupsAsTags(e.target.checked)} />
                <Button size="sm" onClick={() => void addSelected()} loading={busy === 'import'} disabled={busy !== null || toAdd.size === 0}>
                  Add {count(toAdd.size, 'host')} to NKeeper
                </Button>
              </div>
              <p className={styles.healthEmpty}>
                New hosts get Zabbix’s name, IP address, DNS name, inventory MAC address and serial number, description and tags, and
                are linked to their Zabbix host.
              </p>
            </>
          )}
        </section>

        <section className={styles.healthSection} aria-labelledby="nkeeper-only-title">
          <h3 id="nkeeper-only-title" className={styles.healthTitle}>
            Only in NKeeper
          </h3>
          {data.nKeeperOnly.length === 0 ? (
            <p className={styles.healthEmpty}>Zabbix monitors every NKeeper host.</p>
          ) : (
            <table className={styles.healthTable}>
              <thead>
                <tr>
                  <th scope="col">NKeeper host</th>
                  <th scope="col">Addresses</th>
                </tr>
              </thead>
              <tbody>
                {data.nKeeperOnly.map((host) => (
                  <tr key={host.id}>
                    <td>
                      <HostLink host={host} />
                    </td>
                    <td className={styles.mono}>{addresses(host) || '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        {data.brokenLinks.length > 0 ? (
          <section className={styles.healthSection} aria-labelledby="broken-title">
            <h3 id="broken-title" className={styles.healthTitle}>
              Linked to a missing Zabbix host
            </h3>
            <p className={styles.healthEmpty}>
              These hosts are linked to a Zabbix host that no longer exists, or that the API token can’t see.
            </p>
            <table className={styles.healthTable}>
              <thead>
                <tr>
                  <th scope="col">NKeeper host</th>
                  <th scope="col">Addresses</th>
                </tr>
              </thead>
              <tbody>
                {data.brokenLinks.map((host) => (
                  <tr key={host.id}>
                    <td>
                      <HostLink host={host} />
                    </td>
                    <td className={styles.mono}>{addresses(host) || '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div className={own.actions}>
              <Button size="sm" variant="secondary" onClick={() => void unlink(data.brokenLinks)} loading={busy === 'unlink'} disabled={busy !== null}>
                Unlink {count(data.brokenLinks.length, 'host')}
              </Button>
            </div>
          </section>
        ) : null}

        <section className={styles.healthSection} aria-labelledby="linked-title">
          <h3 id="linked-title" className={styles.healthTitle}>
            Linked
          </h3>
          {data.linked.length === 0 ? (
            <p className={styles.healthEmpty}>No hosts are linked to Zabbix yet.</p>
          ) : (
            <details>
              <summary>
                {count(data.linked.length, 'host')} linked to Zabbix
              </summary>
              <table className={styles.healthTable}>
                <thead>
                  <tr>
                    <th scope="col">NKeeper host</th>
                    <th scope="col">Zabbix host</th>
                  </tr>
                </thead>
                <tbody>
                  {data.linked.map((pair) => (
                    <tr key={pair.host.id}>
                      <td>
                        <HostLink host={pair.host} />
                      </td>
                      <td>
                        <ZabbixName zabbix={pair.zabbix} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </details>
          )}
        </section>
      </div>
    </>
  )
}

function count(n: number, noun: string): string {
  return `${n} ${noun}${n === 1 ? '' : 's'}`
}

function addresses(host: ComparedHost): string {
  return [host.hostname, host.ipAddress, host.macAddress].filter(Boolean).join(', ')
}

function HostLink({ host }: { host: ComparedHost }) {
  return (
    <Link to={`/?host=${host.id}`} className={styles.hostLink}>
      {host.name}
    </Link>
  )
}

function ZabbixName({ zabbix }: { zabbix: ZabbixHostSummary }) {
  return (
    <>
      {zabbix.name}
      {zabbix.name !== zabbix.host ? <span className={own.muted}> ({zabbix.host})</span> : null}
      {!zabbix.monitored ? <span className={own.muted}> · not monitored</span> : null}
      <div className={`${styles.mono} ${own.muted}`}>ID {zabbix.hostId}</div>
    </>
  )
}

function Toggle({ id, selected, onChange, label }: { id: string; selected: Set<string>; onChange: (next: Set<string>) => void; label: string }) {
  return (
    <input
      type="checkbox"
      aria-label={label}
      checked={selected.has(id)}
      onChange={(e) => {
        const next = new Set(selected)
        if (e.target.checked) {
          next.add(id)
        } else {
          next.delete(id)
        }
        onChange(next)
      }}
    />
  )
}

function SelectAll({ ids, selected, onChange, label }: { ids: string[]; selected: Set<string>; onChange: (next: Set<string>) => void; label: string }) {
  const all = ids.length > 0 && ids.every((id) => selected.has(id))
  return <input type="checkbox" aria-label={label} checked={all} onChange={() => onChange(all ? new Set() : new Set(ids))} />
}
