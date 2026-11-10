import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../../inventory/api/inventoryApi'
import { useResource } from '../../inventory/hooks/useResource'
import { withInheritedTags } from '../../inventory/inheritance'
import type { Host } from '../../inventory/types'
import { findDuplicates, hostOs, type DuplicateGroup } from '../duplicates'
import styles from './Reports.module.css'

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })

/**
 * Hosts that look like the same machine (same MAC address, else IP address, else hostname, else name), and
 * combining them: one is kept, gets what the others have that it lacks, and the others are deleted.
 */
export function DuplicatesReport() {
  const hosts = useResource((signal) => hostsApi.list(signal))
  const groups = useMemo(() => findDuplicates(withInheritedTags(hosts.data ?? [])), [hosts.data])
  const [done, setDone] = useState<string | null>(null)

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
          <h2 className={styles.reportTitle}>Duplicate hosts</h2>
          <p className={styles.reportSummary}>
            {groups.length === 0
              ? 'No duplicates found.'
              : `${groups.length} group${groups.length === 1 ? '' : 's'} · ${groups.reduce((n, g) => n + g.hosts.length, 0)} hosts`}
          </p>
        </div>
        <div className={styles.reportActions}>
          <Button size="sm" variant="secondary" onClick={hosts.reload} disabled={hosts.loading}>
            Refresh
          </Button>
        </div>
      </header>
      <div className={styles.duplicates}>
        <p className={styles.rowNote}>
          Hosts are duplicates when the first of these that both have matches: MAC address, IP address, hostname (without its domain), name. Two hosts
          with different MAC addresses are never duplicates, and hosts reached through different jump hosts only match by MAC address. Combining keeps
          one host, gives it the tags, services, credentials and missing details of the others, moves their VMs, jump-host links and access grants to
          it, and deletes them.
        </p>
        {done ? <Alert variant="info">{done}</Alert> : null}
        {groups.map((group) => (
          <DuplicateCard
            key={group.id}
            group={group}
            onMerged={(kept, count) => {
              setDone(`Combined ${count} host${count === 1 ? '' : 's'} into ${kept.name}.`)
              hosts.reload()
            }}
          />
        ))}
      </div>
    </>
  )
}

function DuplicateCard({ group, onMerged }: { group: DuplicateGroup; onMerged: (kept: Host, count: number) => void }) {
  const [keepId, setKeepId] = useState(group.hosts[0].id)
  const [combine, setCombine] = useState<Set<string>>(() => new Set(group.hosts.slice(1).map((h) => h.id)))
  const [confirming, setConfirming] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const keep = group.hosts.find((h) => h.id === keepId)!
  const others = group.hosts.filter((h) => h.id !== keepId && combine.has(h.id))
  const canManage = group.hosts.every((h) => h.canManage)

  const choose = (id: string) => {
    setKeepId(id)
    // The one kept isn't combined into itself; the one it replaces is.
    setCombine((current) => new Set([...current, keepId].filter((x) => x !== id)))
  }

  const merge = async () => {
    setSaving(true)
    setError(null)
    try {
      const kept = await hostsApi.merge(keep.id, others.map((h) => h.id))
      setConfirming(false)
      onMerged(kept, others.length)
    } catch (e) {
      setError(toFormErrors(e).message)
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className={styles.duplicateGroup} aria-label={`Duplicates of ${keep.name}`}>
      <p className={styles.duplicateReasons}>{group.reasons.join(' · ')}</p>
      <div className={styles.tableScroll}>
        <table className={styles.grid}>
          <thead>
            <tr>
              <th scope="col">Keep</th>
              <th scope="col">Combine</th>
              <th scope="col">Name</th>
              <th scope="col">Hostname</th>
              <th scope="col">IP address</th>
              <th scope="col">MAC address</th>
              <th scope="col">OS</th>
              <th scope="col">Services</th>
              <th scope="col">Credentials</th>
              <th scope="col">Tags</th>
              <th scope="col">Added</th>
            </tr>
          </thead>
          <tbody>
            {group.hosts.map((host) => (
              <tr key={host.id}>
                <td>
                  <input
                    type="radio"
                    name={`keep-${group.id}`}
                    checked={host.id === keepId}
                    onChange={() => choose(host.id)}
                    aria-label={`Keep ${host.name}`}
                    disabled={!canManage}
                  />
                </td>
                <td>
                  <input
                    type="checkbox"
                    checked={host.id !== keepId && combine.has(host.id)}
                    disabled={host.id === keepId || !canManage}
                    onChange={(e) =>
                      setCombine((current) => {
                        const next = new Set(current)
                        if (e.target.checked) {
                          next.add(host.id)
                        } else {
                          next.delete(host.id)
                        }
                        return next
                      })
                    }
                    aria-label={`Combine ${host.name}`}
                  />
                </td>
                <th scope="row">
                  <Link to={`/?host=${host.id}`} className={styles.hostLink}>
                    {host.name}
                  </Link>
                </th>
                <td>{host.hostname}</td>
                <td>{host.ipAddress}</td>
                <td className={styles.mono}>{host.macAddress}</td>
                <td>{hostOs(host)}</td>
                <td>{host.services.length}</td>
                <td>{host.credentials.length}</td>
                <td>{host.tags.length}</td>
                <td>{dateFormat.format(new Date(host.createdAtUtc))}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {canManage ? (
        <div className={styles.duplicateActions}>
          <Button size="sm" onClick={() => setConfirming(true)} disabled={others.length === 0}>
            {others.length === 0 ? 'Tick hosts to combine' : `Combine ${others.length} into ${keep.name}`}
          </Button>
        </div>
      ) : (
        <p className={styles.rowNote}>You can’t change all of these hosts, so you can’t combine them.</p>
      )}
      {confirming ? (
        <Dialog
          title={`Combine into ${keep.name}?`}
          onClose={() => setConfirming(false)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setConfirming(false)}>
                Cancel
              </Button>
              <Button variant="danger" onClick={merge} loading={saving}>
                Combine and delete {others.length === 1 ? '1 host' : `${others.length} hosts`}
              </Button>
            </>
          }
        >
          <p>
            {keep.name} keeps its own details and gets the tags, services, credentials and missing details of{' '}
            <b>{others.map((h) => h.name).join(', ')}</b>. VMs, hosts reached through them and access given for them move to {keep.name}.
          </p>
          <p>
            Then {others.length === 1 ? 'it is' : 'they are'} <b>deleted</b>. This can’t be undone.
          </p>
          {error ? <Alert>{error}</Alert> : null}
        </Dialog>
      ) : null}
    </section>
  )
}
