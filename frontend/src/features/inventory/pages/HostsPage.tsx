import { useCallback, useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Badge } from '../../../components/ui/Badge'
import { Button } from '../../../components/ui/Button'
import { Select } from '../../../components/ui/Select'
import { Spinner } from '../../../components/ui/Spinner'
import { TextField } from '../../../components/ui/TextField'
import { toFormErrors } from '../../../lib/formErrors'
import { groupsApi, hostsApi, tagsApi } from '../api/inventoryApi'
import { HostFormDialog } from '../components/HostFormDialog'
import { SortableHeader } from '../components/SortableHeader'
import { useResource } from '../hooks/useResource'
import {
  PROTOCOL_INFO,
  SERVICE_PROTOCOLS,
  type HostListQuery,
  type HostService,
  type HostSortField,
  type ServiceProtocol,
  type SortDirection,
} from '../types'
import styles from './InventoryPage.module.css'

const PAGE_SIZE = 50
const SEARCH_DEBOUNCE_MS = 300
const SORT_FIELDS: HostSortField[] = ['name', 'address', 'group', 'updated']
const UNGROUPED = 'none'

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })
const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** Reads the list state from the URL so filtered views can be bookmarked and shared. */
function readQuery(params: URLSearchParams): { query: HostListQuery; group: string } {
  const sort = params.get('sort') as HostSortField | null
  const protocol = params.get('service') as ServiceProtocol | null
  const page = Number.parseInt(params.get('page') ?? '', 10)
  const group = params.get('group') ?? ''
  const query: HostListQuery = {
    search: params.get('q') ?? '',
    groupId: group && group !== UNGROUPED ? group : undefined,
    ungrouped: group === UNGROUPED || undefined,
    tag: params.get('tag') ?? undefined,
    protocol: protocol && SERVICE_PROTOCOLS.includes(protocol) ? protocol : undefined,
    sort: sort && SORT_FIELDS.includes(sort) ? sort : 'name',
    direction: params.get('dir') === 'desc' ? 'desc' : 'asc',
    page: Number.isFinite(page) && page > 1 ? page : 1,
    pageSize: PAGE_SIZE,
  }
  return { query, group }
}

function serviceText(service: HostService): string {
  const info = PROTOCOL_INFO[service.protocol]
  const port = service.port && service.port !== info.defaultPort ? `:${service.port}` : ''
  return `${info.label}${port}`
}

export function HostsPage() {
  const [params, setParams] = useSearchParams()
  const { query, group } = readQuery(params)
  const [searchDraft, setSearchDraft] = useState(query.search ?? '')
  const [editing, setEditing] = useState<string | 'new' | null>(null)

  const hosts = useResource(
    (signal) => hostsApi.list({ ...query, search: query.search || undefined }, signal),
    params.toString(),
  )
  const groups = useResource((signal) => groupsApi.list(signal))
  const tags = useResource((signal) => tagsApi.list(signal))

  /** Applies changes to the URL; any filter change returns to page 1. */
  const update = useCallback(
    (changes: Record<string, string | null>, { resetPage = true } = {}) =>
      setParams(
      (current) => {
        const next = new URLSearchParams(current)
        for (const [key, value] of Object.entries(changes)) {
          if (value) {
            next.set(key, value)
          } else {
            next.delete(key)
          }
        }
        if (resetPage) {
          next.delete('page')
        }
        return next
      },
      { replace: true },
    ),
    [setParams],
  )

  // Debounce typing into the URL (which triggers the fetch).
  const currentSearch = query.search ?? ''
  const pushedSearch = useRef(currentSearch)
  useEffect(() => {
    const term = searchDraft.trim()
    if (term === pushedSearch.current) {
      return
    }
    const timer = setTimeout(() => {
      pushedSearch.current = term
      update({ q: term || null })
    }, SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [searchDraft, update])

  // Mirror URL changes made elsewhere (back button, clear filters) into the box,
  // but never overwrite text the user is still typing.
  useEffect(() => {
    if (currentSearch !== pushedSearch.current) {
      pushedSearch.current = currentSearch
      setSearchDraft(currentSearch)
    }
  }, [currentSearch])

  function sortBy(field: HostSortField) {
    const direction: SortDirection =
      query.sort === field ? (query.direction === 'asc' ? 'desc' : 'asc') : field === 'updated' ? 'desc' : 'asc'
    update({ sort: field === 'name' ? null : field, dir: direction === 'asc' ? null : 'desc' })
  }

  function refreshAll() {
    hosts.reload()
    groups.reload()
    tags.reload()
  }

  const hasFilters = Boolean(query.search || group || query.tag || query.protocol)
  const result = hosts.data
  const totalPages = result ? Math.max(1, Math.ceil(result.totalCount / result.pageSize)) : 1
  const firstIndex = result && result.totalCount > 0 ? (result.page - 1) * result.pageSize + 1 : 0
  const lastIndex = result ? firstIndex + result.items.length - 1 : 0

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <div>
          <h1 className={styles.title}>Hosts</h1>
          <p className={styles.subtitle}>Servers and devices, and the ways you connect to them.</p>
        </div>
        <Button onClick={() => setEditing('new')}>Add host</Button>
      </div>

      <div className={styles.toolbar} role="search">
        <TextField
          className={styles.search}
          label="Search hosts"
          hideLabel
          type="search"
          value={searchDraft}
          onChange={(event) => setSearchDraft(event.target.value)}
          placeholder="Search name, address, MAC, group, tag…"
          maxLength={200}
        />
        <Select label="Group" hideLabel value={group} onChange={(e) => update({ group: e.target.value || null })}>
          <option value="">All groups</option>
          <option value={UNGROUPED}>Ungrouped</option>
          {groups.data?.map((group) => (
            <option key={group.id} value={group.id}>
              {group.name}
            </option>
          ))}
        </Select>
        <Select label="Tag" hideLabel value={query.tag ?? ''} onChange={(e) => update({ tag: e.target.value || null })}>
          <option value="">All tags</option>
          {query.tag && !tags.data?.some((t) => t.name.toLowerCase() === query.tag?.toLowerCase()) ? (
            <option value={query.tag}>{query.tag}</option>
          ) : null}
          {tags.data?.map((tag) => (
            <option key={tag.name} value={tag.name}>
              {tag.name}
            </option>
          ))}
        </Select>
        <Select
          label="Service"
          hideLabel
          value={query.protocol ?? ''}
          onChange={(e) => update({ service: e.target.value || null })}
        >
          <option value="">All services</option>
          {SERVICE_PROTOCOLS.map((protocol) => (
            <option key={protocol} value={protocol}>
              {PROTOCOL_INFO[protocol].name}
            </option>
          ))}
        </Select>
        {hasFilters ? (
          <Button
            variant="ghost"
            onClick={() => {
              setSearchDraft('')
              update({ q: null, group: null, tag: null, service: null })
            }}
          >
            Clear filters
          </Button>
        ) : null}
      </div>

      {hosts.error ? (
        <Alert>
          {toFormErrors(hosts.error).message}{' '}
          <button type="button" className={styles.linkButton} onClick={hosts.reload}>
            Try again
          </button>
        </Alert>
      ) : null}

      <div className={styles.tableCard} aria-busy={hosts.loading}>
        {result === undefined && hosts.loading ? (
          <div className={styles.state}>
            <Spinner label="Loading hosts" />
          </div>
        ) : result && result.items.length === 0 ? (
          <div className={styles.state}>
            {hasFilters ? (
              <>
                <p className={styles.stateTitle}>No hosts match these filters</p>
                <p>Try a different search, or clear the filters.</p>
              </>
            ) : (
              <>
                <p className={styles.stateTitle}>No hosts yet</p>
                <p>Add your first server to start building your inventory.</p>
                <Button onClick={() => setEditing('new')}>Add host</Button>
              </>
            )}
          </div>
        ) : result ? (
          <div className={styles.tableScroll}>
            <table className={styles.table}>
              <caption className="visually-hidden">
                Hosts, sorted by {query.sort} {query.direction === 'asc' ? 'ascending' : 'descending'}
              </caption>
              <thead>
                <tr>
                  <SortableHeader field="name" label="Name" query={query} onSort={sortBy} />
                  <SortableHeader field="address" label="Address" query={query} onSort={sortBy} />
                  <SortableHeader field="group" label="Group" query={query} onSort={sortBy} className={styles.hideSmall} />
                  <th scope="col">Services</th>
                  <th scope="col" className={styles.hideSmall}>
                    Tags
                  </th>
                  <th scope="col" className={styles.hideMedium}>
                    <abbr title="Credentials">Creds</abbr>
                  </th>
                  <SortableHeader field="updated" label="Updated" query={query} onSort={sortBy} className={styles.hideMedium} />
                </tr>
              </thead>
              <tbody>
                {result.items.map((host) => (
                  <tr key={host.id}>
                    <td>
                      <button type="button" className={styles.rowLink} onClick={() => setEditing(host.id)}>
                        {host.name}
                      </button>
                    </td>
                    <td className={styles.mono}>
                      {host.hostname ? <div>{host.hostname}</div> : null}
                      {host.ipAddress ? <div className={host.hostname ? styles.secondary : undefined}>{host.ipAddress}</div> : null}
                    </td>
                    <td className={styles.hideSmall}>
                      {host.group ? (
                        <button type="button" className={styles.filterLink} onClick={() => update({ group: host.group!.id })}>
                          {host.group.name}
                        </button>
                      ) : (
                        <span className={styles.muted}>—</span>
                      )}
                    </td>
                    <td>
                      <div className={styles.badges}>
                        {host.services.length === 0 ? <span className={styles.muted}>—</span> : null}
                        {host.services.map((service, index) => (
                          <Badge key={index} variant={index === 0 ? 'accent' : 'neutral'} title={service.label ?? PROTOCOL_INFO[service.protocol].name}>
                            {serviceText(service)}
                          </Badge>
                        ))}
                      </div>
                    </td>
                    <td className={styles.hideSmall}>
                      <div className={styles.badges}>
                        {host.tags.map((tag) => (
                          <button
                            key={tag}
                            type="button"
                            className={styles.tagButton}
                            onClick={() => update({ tag })}
                            aria-label={`Filter by tag ${tag}`}
                          >
                            <Badge variant="tag">{tag}</Badge>
                          </button>
                        ))}
                      </div>
                    </td>
                    <td className={`${styles.hideMedium} ${styles.numeric}`}>
                      {host.credentialCount > 0 ? host.credentialCount : <span className={styles.muted}>—</span>}
                    </td>
                    <td className={`${styles.hideMedium} ${styles.nowrap}`}>
                      <time dateTime={host.updatedAtUtc} title={dateTimeFormat.format(new Date(host.updatedAtUtc))}>
                        {dateFormat.format(new Date(host.updatedAtUtc))}
                      </time>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}

        {result && result.totalCount > 0 ? (
          <div className={styles.pager}>
            <span className={styles.muted} aria-live="polite">
              {result.totalCount === result.items.length
                ? `${result.totalCount} ${result.totalCount === 1 ? 'host' : 'hosts'}`
                : `${firstIndex}–${lastIndex} of ${result.totalCount} hosts`}
            </span>
            {totalPages > 1 ? (
              <div className={styles.pagerButtons}>
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={result.page <= 1}
                  onClick={() => update({ page: result.page > 2 ? String(result.page - 1) : null }, { resetPage: false })}
                >
                  Previous
                </Button>
                <span className={styles.muted}>
                  Page {result.page} of {totalPages}
                </span>
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={result.page >= totalPages}
                  onClick={() => update({ page: String(result.page + 1) }, { resetPage: false })}
                >
                  Next
                </Button>
              </div>
            ) : null}
          </div>
        ) : null}
      </div>

      {editing ? (
        <HostFormDialog hostId={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onChanged={refreshAll} />
      ) : null}
    </div>
  )
}
