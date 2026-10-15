import { useMemo, useState } from 'react'
import { useSearchParams } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../api/inventoryApi'
import { GroupByPicker } from '../components/GroupByPicker'
import { HostDetails } from '../components/HostDetails'
import { HostEditor } from '../components/HostEditor'
import { HostTree } from '../components/HostTree'
import { buildTree, decodeGroupKey, encodeGroupKey, sortHosts, tagNames, tagValues, type GroupKey, type HostSort } from '../grouping'
import { useResource } from '../hooks/useResource'
import { matchesSearch, parseSearch, tagSearchTerm } from '../search'
import type { Host, HostTag } from '../types'
import styles from './WorkspacePage.module.css'

const NEW_HOST = 'new'
const SORTS: { value: HostSort; label: string }[] = [
  { value: 'name', label: 'Name' },
  { value: 'address', label: 'Address' },
  { value: 'updated', label: 'Recently updated' },
]

/** Saved or deleted hosts applied on top of the last fetched list, so edits show instantly. */
interface LocalChanges {
  basedOn: Host[] | undefined
  hosts: Host[]
}

/**
 * The single-page workspace: a searchable, groupable host tree on the left and the selected
 * host's details (or editor) on the right. View state lives in the URL, e.g.
 * `/?group=site&group=role&q=env:prod&host=<id>`, so views can be bookmarked.
 */
export function WorkspacePage() {
  const [params, setParams] = useSearchParams()
  const resource = useResource((signal) => hostsApi.list(signal))
  const [local, setLocal] = useState<LocalChanges>({ basedOn: undefined, hosts: [] })
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set())

  const allHosts = local.basedOn !== undefined && local.basedOn === resource.data ? local.hosts : resource.data
  const query = params.get('q') ?? ''
  const groupKeys = useMemo(() => params.getAll('group').map(decodeGroupKey), [params])
  const sort = (SORTS.find((s) => s.value === params.get('sort'))?.value ?? 'name') as HostSort
  const selectedId = params.get('host')
  const editing = selectedId === NEW_HOST || params.get('edit') === '1'

  const visibleHosts = useMemo(() => {
    const terms = parseSearch(query)
    return sortHosts((allHosts ?? []).filter((host) => matchesSearch(host, terms)), sort)
  }, [allHosts, query, sort])
  const tree = useMemo(() => buildTree(visibleHosts, groupKeys), [visibleHosts, groupKeys])
  const knownTagNames = useMemo(() => tagNames(allHosts ?? []), [allHosts])
  const selectedHost = selectedId && selectedId !== NEW_HOST ? allHosts?.find((h) => h.id === selectedId) : undefined

  /** Updates URL view state. `null` removes a parameter; arrays set repeated parameters. */
  function update(changes: Record<string, string | string[] | null>) {
    setParams(
      (current) => {
        const next = new URLSearchParams(current)
        for (const [key, value] of Object.entries(changes)) {
          next.delete(key)
          for (const item of Array.isArray(value) ? value : value ? [value] : []) {
            next.append(key, item)
          }
        }
        return next
      },
      { replace: true },
    )
  }

  function applyLocally(change: (hosts: Host[]) => Host[]) {
    setLocal({ basedOn: resource.data, hosts: change(allHosts ?? []) })
  }

  function handleSaved(saved: Host) {
    applyLocally((hosts) => [...hosts.filter((h) => h.id !== saved.id), saved])
    update({ host: saved.id, edit: null })
  }

  function handleDeleted(id: string) {
    applyLocally((hosts) => hosts.filter((h) => h.id !== id))
    update({ host: null, edit: null })
  }

  function filterByTag(tag: HostTag) {
    update({ q: tagSearchTerm(tag.name, tag.value) })
  }

  function toggleGroup(id: string) {
    setCollapsed((current) => {
      const next = new Set(current)
      if (!next.delete(id)) {
        next.add(id)
      }
      return next
    })
  }

  const showingPanel = selectedId !== null

  return (
    <div className={`${styles.workspace} ${showingPanel ? styles.panelOpen : ''}`}>
      <aside className={styles.sidebar} aria-label="Host inventory">
        <div className={styles.sidebarHeader}>
          <h1 className={styles.sidebarTitle}>
            Hosts
            {allHosts ? (
              <span className={styles.total}>
                {visibleHosts.length === allHosts.length ? allHosts.length : `${visibleHosts.length} of ${allHosts.length}`}
              </span>
            ) : null}
          </h1>
          <div className={styles.sidebarActions}>
            <button
              type="button"
              className={styles.iconButton}
              onClick={resource.reload}
              aria-label="Refresh hosts"
              title="Refresh"
              disabled={resource.loading}
            >
              <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">
                <path
                  d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9M13.5 2.5v3h-3"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="1.5"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                />
              </svg>
            </button>
            <Button size="sm" onClick={() => update({ host: NEW_HOST, edit: null })}>
              New host
            </Button>
          </div>
        </div>

        <div className={styles.controls}>
          <input
            className={styles.search}
            type="search"
            value={query}
            onChange={(event) => update({ q: event.target.value || null })}
            placeholder="Search, or env:prod site:&quot;DC A&quot;"
            aria-label="Search hosts"
            spellCheck={false}
          />
          <GroupByPicker
            value={groupKeys}
            onChange={(keys: GroupKey[]) => {
              setCollapsed(new Set())
              update({ group: keys.map(encodeGroupKey) })
            }}
            tagNames={knownTagNames}
          />
          <label className={styles.sort}>
            <span>Sort</span>
            <select value={sort} onChange={(event) => update({ sort: event.target.value === 'name' ? null : event.target.value })}>
              {SORTS.map((s) => (
                <option key={s.value} value={s.value}>
                  {s.label}
                </option>
              ))}
            </select>
          </label>
        </div>

        <div className={styles.treeScroll}>
          {resource.error && !allHosts ? (
            <div className={styles.sidebarState}>
              <Alert>{toFormErrors(resource.error).message}</Alert>
              <Button variant="secondary" size="sm" onClick={resource.reload}>
                Try again
              </Button>
            </div>
          ) : !allHosts ? (
            <div className={styles.sidebarState}>
              <Spinner label="Loading hosts" />
            </div>
          ) : allHosts.length === 0 ? (
            <p className={styles.sidebarState}>No hosts yet.</p>
          ) : visibleHosts.length === 0 ? (
            <div className={styles.sidebarState}>
              <p>No hosts match “{query}”.</p>
              <Button variant="ghost" size="sm" onClick={() => update({ q: null })}>
                Clear search
              </Button>
            </div>
          ) : (
            <HostTree
              nodes={tree}
              selectedHostId={selectedId}
              collapsed={collapsed}
              onToggle={toggleGroup}
              onSelect={(id) => update({ host: id, edit: null })}
            />
          )}
        </div>
      </aside>

      <main className={styles.main}>
        {showingPanel ? (
          <button type="button" className={styles.back} onClick={() => update({ host: null, edit: null })}>
            ‹ All hosts
          </button>
        ) : null}

        {editing && (selectedId === NEW_HOST || selectedHost) ? (
          <HostEditor
            key={selectedId}
            host={selectedHost ?? null}
            knownTagNames={knownTagNames}
            knownTagValues={(name) => tagValues(allHosts ?? [], name)}
            onSaved={handleSaved}
            onCancel={() => update(selectedId === NEW_HOST ? { host: null } : { edit: null })}
          />
        ) : selectedHost ? (
          <HostDetails
            key={selectedHost.id}
            host={selectedHost}
            onEdit={() => update({ edit: '1' })}
            onDeleted={handleDeleted}
            onTagClick={filterByTag}
          />
        ) : selectedId && allHosts ? (
          <div className={styles.mainState}>
            <p className={styles.stateTitle}>Host not found</p>
            <p>It may have been deleted. Choose another host from the tree.</p>
          </div>
        ) : (
          <div className={styles.mainState}>
            {allHosts?.length === 0 ? (
              <>
                <p className={styles.stateTitle}>Your inventory is empty</p>
                <p>Add your first host, with its services, credentials and tags.</p>
                <Button onClick={() => update({ host: NEW_HOST })}>New host</Button>
              </>
            ) : (
              <>
                <p className={styles.stateTitle}>Select a host</p>
                <p>
                  Search by name, address or tag (<code>env:prod</code>), and group the tree by any tag, such as site then role.
                </p>
              </>
            )}
          </div>
        )}
      </main>
    </div>
  )
}
