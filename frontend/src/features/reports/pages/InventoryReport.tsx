import { useMemo } from 'react'
import { Link, useSearchParams } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../../inventory/api/inventoryApi'
import { GroupByPicker } from '../../inventory/components/GroupByPicker'
import { decodeGroupKey, encodeGroupKey, groupKeyLabel, sortHosts, tagNames, type GroupKey } from '../../inventory/grouping'
import { useResource } from '../../inventory/hooks/useResource'
import { downloadCsv } from '../download'
import { buildInventory, inventoryCsv, type InventoryRow } from '../inventoryCounts'
import styles from './Reports.module.css'

const MAX_LEVELS = 5

/**
 * How many hosts have each combination of values of the chosen fields: tags, operating system,
 * services, usernames, the host VMs run on, physical or VM. The fields live in the query string as
 * the workspace's do (`?group=site&group=@service`) so the report can be bookmarked. Each row opens
 * the workspace grouped the same way, filtered to its tag values.
 */
export function InventoryReport() {
  const hosts = useResource((signal) => hostsApi.list(signal))
  const [params, setParams] = useSearchParams()
  const keys = useMemo(() => params.getAll('group').map(decodeGroupKey), [params])
  const sorted = useMemo(() => sortHosts(hosts.data ?? [], 'name'), [hosts.data])
  const report = useMemo(() => buildInventory(sorted, keys), [sorted, keys])
  const names = useMemo(() => tagNames(hosts.data ?? []), [hosts.data])

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

  const setKeys = (chosen: GroupKey[]) =>
    setParams(
      (current) => {
        const next = new URLSearchParams(current)
        next.delete('group')
        chosen.forEach((key) => next.append('group', encodeGroupKey(key)))
        return next
      },
      { replace: true },
    )

  return (
    <>
      <header className={styles.reportHeader}>
        <div>
          <h2 className={styles.reportTitle}>Inventory</h2>
          <p className={styles.reportSummary}>
            {keys.length === 0
              ? 'Choose one or more fields to count hosts by.'
              : `${plural(report.rows.length, 'combination')} · ${plural(report.hostCount, 'host')}`}
          </p>
        </div>
        <div className={styles.reportActions}>
          <Button size="sm" variant="secondary" onClick={hosts.reload} disabled={hosts.loading}>
            Refresh
          </Button>
          <Button size="sm" onClick={() => downloadCsv('nkeeper-inventory.csv', inventoryCsv(report))} disabled={report.rows.length === 0}>
            Download CSV
          </Button>
        </div>
      </header>

      <div className={styles.toolbar}>
        <GroupByPicker value={keys} onChange={setKeys} tagNames={names} maxLevels={MAX_LEVELS} />
      </div>

      {hosts.data.length === 0 ? (
        <p className={styles.state}>No hosts yet.</p>
      ) : report.rows.length === 0 ? null : (
        <div className={styles.tableScroll}>
          <table className={`${styles.grid} ${styles.inventory}`}>
            <thead>
              <tr>
                {keys.map((key, index) => (
                  <th key={encodeGroupKey(key)} scope="col" className={index === 0 ? styles.corner : undefined}>
                    {groupKeyLabel(key)}
                  </th>
                ))}
                <th scope="col" className={styles.count}>
                  Hosts
                </th>
                <th scope="col">Share</th>
              </tr>
            </thead>
            <tbody>
              {report.rows.map((row, index) => (
                <CountRow key={row.id} row={row} previous={report.rows[index - 1]} keys={keys} total={report.hostCount} />
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th scope="row" colSpan={keys.length}>
                  All hosts
                </th>
                <td className={styles.count}>{report.hostCount}</td>
                <td />
              </tr>
            </tfoot>
          </table>
        </div>
      )}
    </>
  )
}

/** A combination's values (repeats of the row above dimmed, like an outline), its count and its share of all hosts. */
function CountRow({ row, previous, keys, total }: { row: InventoryRow; previous?: InventoryRow; keys: GroupKey[]; total: number }) {
  const percent = total > 0 ? Math.round((row.hosts.length / total) * 100) : 0
  const workspace = new URLSearchParams([...keys.map((key) => ['group', encodeGroupKey(key)]), ...(row.search ? [['q', row.search]] : [])])
  // A value repeats when it and every value before it match the row above.
  const repeats = previous ? row.values.findIndex((value, i) => previous.values[i].label !== value.label) : 0
  const repeatCount = repeats === -1 ? row.values.length : repeats
  return (
    <tr>
      {row.values.map((value, i) => {
        const Cell = i === 0 ? 'th' : 'td'
        const className = [i < repeatCount ? styles.repeat : '', value.missing ? styles.missing : ''].filter(Boolean).join(' ')
        return (
          <Cell key={i} scope={i === 0 ? 'row' : undefined} className={className || undefined}>
            {value.label}
          </Cell>
        )
      })}
      <td className={styles.count}>
        <Link to={`/?${workspace}`} className={styles.hostLink} title={row.hosts.map((h) => h.name).join(', ')}>
          {row.hosts.length}
        </Link>
      </td>
      <td>
        <span className={styles.shareBar} aria-hidden="true">
          <span style={{ width: `${percent}%` }} />
        </span>
        {percent}%
      </td>
    </tr>
  )
}

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? '' : 's'}`
}
