import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../../inventory/api/inventoryApi'
import { useResource } from '../../inventory/hooks/useResource'
import type { Host } from '../../inventory/types'
import { downloadCsv } from '../download'
import { buildIpGrid, ipGridCsv, OCTETS } from '../ipGrid'
import styles from './Reports.module.css'

/**
 * Every host by IPv4 address, as a spreadsheet: one column per /24 subnet that has hosts, one row
 * per last octet (.0–.255), and each host's name where it sits. Two hosts on one address are
 * flagged. Only hosts the user can see are included. Click a name to open the host.
 */
export function IpAddressReport() {
  const hosts = useResource((signal) => hostsApi.list(signal))
  const [usedOnly, setUsedOnly] = useState(false)
  const grid = useMemo(() => buildIpGrid(hosts.data ?? []), [hosts.data])
  const octets = usedOnly ? OCTETS.filter((o) => grid.usedOctets.has(o)) : OCTETS
  const skipped = (hosts.data ?? []).filter((h) => !h.ipAddress || h.ipAddress.includes(':')).length

  if (hosts.error && !hosts.data) {
    return (
      <div className={styles.state}>
        <Alert>{toFormErrors(hosts.error).message}</Alert>
        <Button variant="secondary" size="sm" onClick={hosts.reload}>
          Try again
        </Button>
      </div>
    )
  }

  if (!hosts.data) {
    return (
      <div className={styles.state}>
        <Spinner label="Loading hosts" />
      </div>
    )
  }

  return (
    <>
      <header className={styles.reportHeader}>
        <div>
          <h2 className={styles.reportTitle}>IP address map</h2>
          <p className={styles.reportSummary}>
            {grid.subnets.length === 1 ? '1 subnet' : `${grid.subnets.length} subnets`} (/24)
            {skipped > 0 ? ` · ${skipped} ${skipped === 1 ? 'host' : 'hosts'} without an IPv4 address not shown` : ''}
          </p>
        </div>
        <div className={styles.reportActions}>
          <label className={styles.toggle}>
            <input type="checkbox" checked={usedOnly} onChange={(event) => setUsedOnly(event.target.checked)} />
            Only used addresses
          </label>
          <Button size="sm" variant="secondary" onClick={hosts.reload} disabled={hosts.loading}>
            Refresh
          </Button>
          <Button size="sm" onClick={() => downloadCsv('nkeeper-ip-addresses.csv', ipGridCsv(grid, octets))} disabled={grid.subnets.length === 0}>
            Download CSV
          </Button>
        </div>
      </header>

      {grid.subnets.length === 0 ? (
        <p className={styles.state}>No hosts have an IPv4 address yet.</p>
      ) : (
        <div className={styles.tableScroll}>
          <table className={styles.grid}>
            <thead>
              <tr>
                <th scope="col" className={styles.corner}>
                  Address
                </th>
                {grid.subnets.map((subnet) => (
                  <th key={subnet.prefix} scope="col">
                    {subnet.prefix}.x<span className={styles.used}>{subnet.used} used</span>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {octets.map((octet) => (
                <tr key={octet}>
                  <th scope="row">.{octet}</th>
                  {grid.subnets.map((subnet) => (
                    <Cell key={subnet.prefix} hosts={grid.at(subnet.prefix, octet)} address={`${subnet.prefix}.${octet}`} />
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  )
}

function Cell({ hosts, address }: { hosts: Host[]; address: string }) {
  if (hosts.length === 0) {
    return <td />
  }
  const conflict = hosts.length > 1
  return (
    <td className={conflict ? styles.conflict : undefined} title={conflict ? `${address} is used by ${hosts.length} hosts` : undefined}>
      {hosts.map((host) => (
        <Link key={host.id} to={`/?host=${host.id}`} className={styles.hostLink} title={[host.name, host.hostname, address].filter(Boolean).join(' · ')}>
          {host.name}
        </Link>
      ))}
    </td>
  )
}
