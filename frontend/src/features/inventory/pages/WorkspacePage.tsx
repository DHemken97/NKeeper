import { lazy, Suspense, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { ContextMenu } from '../../../components/ui/ContextMenu'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { hostsApi } from '../api/inventoryApi'
import { GroupByPicker } from '../components/GroupByPicker'
import { HostDetails } from '../components/HostDetails'
import { HostEditor } from '../components/HostEditor'
import { HostTree } from '../components/HostTree'
import { buildTree, decodeGroupKey, encodeGroupKey, sortHosts, tagNames, tagValues, type GroupKey, type HostSort } from '../grouping'
import { CredentialPromptDialog } from '../../terminal/components/CredentialPromptDialog'
import type { VirtualMachine } from '../../terminal/kvm'
import type { RemoteSession } from '../../terminal/types'
import { useResource } from '../hooks/useResource'
import { connectMenuEntries, defaultConnection, directWebUrl, hostMenuEntries, serviceLabel, WEB_PROTOCOLS, type HostMenuActions } from '../hostMenu'
import { matchesSearch, parseSearch, tagSearchTerm } from '../search'
import { formatAccount, primaryAddress, type Host, type HostCredential, type HostTag, type ServiceProtocol } from '../types'
import { useAuth } from '../../auth/context'
import { isAdministrator } from '../../auth/types'
import styles from './WorkspacePage.module.css'

const NEW_HOST = 'new'
const DETAILS_TAB = 'details'
let nextSessionId = 1

// xterm.js is large; load it on the first connection rather than with the page.
const SshTerminal = lazy(() => import('../../terminal/components/SshTerminal').then((m) => ({ default: m.SshTerminal })))
const WebSession = lazy(() => import('../../terminal/components/WebSession').then((m) => ({ default: m.WebSession })))
const DesktopSession = lazy(() => import('../../terminal/components/DesktopSession').then((m) => ({ default: m.DesktopSession })))
const VirtualMachinesView = lazy(() =>
  import('../../terminal/components/VirtualMachinesView').then((m) => ({ default: m.VirtualMachinesView })),
)
const SpiceConsole = lazy(() => import('../../terminal/components/SpiceConsole').then((m) => ({ default: m.SpiceConsole })))
const VncConsole = lazy(() => import('../../terminal/components/VncConsole').then((m) => ({ default: m.VncConsole })))
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

/** Which session view a service opens in. */
function sessionProtocol(protocol: ServiceProtocol): RemoteSession['protocol'] {
  if (WEB_PROTOCOLS.has(protocol)) {
    return 'web'
  }
  return protocol === 'Rdp' ? 'rdp' : protocol === 'Vnc' ? 'vnc' : protocol === 'Kvm' ? 'kvm' : 'ssh'
}

interface MenuState {
  hostId: string
  x: number
  y: number
  /** Only the Connect entries (from the details panel's Connect button). */
  connectOnly?: boolean
}

/**
 * The single-page workspace: a searchable, groupable host tree on the left; on the right, the
 * selected host's details (or editor) and a tab per open terminal session. Right-click a host
 * for Connect > service > credential. View state lives in the URL, e.g.
 * `/?group=site&group=role&q=env:prod&host=<id>`, so views can be bookmarked; sessions do not.
 */
export function WorkspacePage() {
  const [params, setParams] = useSearchParams()
  const { user } = useAuth()
  const admin = isAdministrator(user)
  const resource = useResource((signal) => hostsApi.list(signal))
  const [local, setLocal] = useState<LocalChanges>({ basedOn: undefined, hosts: [] })
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set())
  const [sessions, setSessions] = useState<RemoteSession[]>([])
  const [activeTab, setActiveTab] = useState(DETAILS_TAB)
  const [menu, setMenu] = useState<MenuState | null>(null)
  const [prompt, setPrompt] = useState<{ host: Host; serviceIndex: number } | null>(null)

  const allHosts = local.basedOn !== undefined && local.basedOn === resource.data ? local.hosts : resource.data
  const query = params.get('q') ?? ''
  const groupKeys = useMemo(() => params.getAll('group').map(decodeGroupKey), [params])
  const sort = (SORTS.find((s) => s.value === params.get('sort'))?.value ?? 'name') as HostSort
  const selectedId = params.get('host')
  const editRequested = selectedId === NEW_HOST || params.get('edit') === '1'

  const visibleHosts = useMemo(() => {
    const terms = parseSearch(query)
    return sortHosts((allHosts ?? []).filter((host) => matchesSearch(host, terms)), sort)
  }, [allHosts, query, sort])
  const tree = useMemo(() => buildTree(visibleHosts, groupKeys), [visibleHosts, groupKeys])
  const knownTagNames = useMemo(() => tagNames(allHosts ?? []), [allHosts])
  const selectedHost = selectedId && selectedId !== NEW_HOST ? allHosts?.find((h) => h.id === selectedId) : undefined
  const cloneId = selectedId === NEW_HOST ? params.get('clone') : null
  const cloneSource = cloneId ? allHosts?.find((h) => h.id === cloneId) : undefined
  // Only administrators add hosts, and only hosts the user may manage can be edited.
  const editing = editRequested && (selectedId === NEW_HOST ? admin : selectedHost?.canManage === true)

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

  function showHost(id: string | null, edit = false) {
    update({ host: id, edit: edit ? '1' : null, clone: null })
    setActiveTab(DETAILS_TAB)
  }

  /** Opens the editor for a new host prefilled from this one. */
  function cloneHost(host: Host) {
    update({ host: NEW_HOST, edit: null, clone: host.id })
    setActiveTab(DETAILS_TAB)
  }

  function openSession(host: Host, serviceIndex: number, credential?: HostCredential, oneOff?: { username: string; password: string }) {
    const id = `session-${nextSessionId++}`
    const account = credential ? (formatAccount(credential) ?? credential.label) : oneOff?.username
    const sameHost = sessions.filter((s) => s.hostId === host.id).length
    setSessions((current) => [
      ...current,
      {
        id,
        hostId: host.id,
        protocol: sessionProtocol(host.services[serviceIndex].protocol),
        title: sameHost > 0 ? `${host.name} (${sameHost + 1})` : host.name,
        detail: [serviceLabel(host.services[serviceIndex]), account].filter(Boolean).join(' · '),
        request: { hostId: host.id, serviceIndex, credentialId: credential?.id, oneOff },
      },
    ])
    setActiveTab(id)
  }

  /** Opens a VM's console from a KVM host's tab, with the same login. */
  function openConsole(source: RemoteSession, vm: VirtualMachine) {
    const id = `session-${nextSessionId++}`
    const protocol = vm.display === 'vnc' ? 'vmvnc' : 'spice'
    // spice-html5 gives its canvases page-wide IDs, so one SPICE console is open at a time: it replaces the last.
    setSessions((current) => [
      ...current.filter((s) => protocol !== 'spice' || s.protocol !== 'spice'),
      {
        id,
        hostId: source.hostId,
        protocol,
        title: vm.name,
        detail: `Console · ${source.title}`,
        request: { ...source.request, vm: { uuid: vm.uuid, name: vm.name } },
      },
    ])
    setActiveTab(id)
  }

  function closeSession(id: string) {
    const index = sessions.findIndex((s) => s.id === id)
    const remaining = sessions.filter((s) => s.id !== id)
    setSessions(remaining)
    if (activeTab === id) {
      setActiveTab(remaining[Math.min(index, remaining.length - 1)]?.id ?? DETAILS_TAB)
    }
  }

  const menuActions: HostMenuActions = {
    connect: (host, serviceIndex, credential) => openSession(host, serviceIndex, credential),
    connectWithPrompt: (host, serviceIndex) => setPrompt({ host, serviceIndex }),
    connectWeb: (host, serviceIndex) => openSession(host, serviceIndex),
    // Called from the menu click itself, so the browser allows the new tab.
    openDirect: (host, serviceIndex) => void window.open(directWebUrl(host, host.services[serviceIndex]), '_blank', 'noopener,noreferrer'),
    edit: (host) => showHost(host.id, true),
    clone: admin ? cloneHost : undefined,
    copyAddress: (host) => void navigator.clipboard?.writeText(primaryAddress(host)),
  }

  /** Double-click: connect with the first SSH service and credential, or ask for one. */
  function connectWithDefaults(hostId: string) {
    const host = allHosts?.find((h) => h.id === hostId)
    const connection = host ? defaultConnection(host) : null
    if (!host || !connection) {
      showHost(hostId)
    } else if (WEB_PROTOCOLS.has(host.services[connection.serviceIndex].protocol) || connection.credential) {
      openSession(host, connection.serviceIndex, connection.credential ?? undefined)
    } else {
      setPrompt({ host, serviceIndex: connection.serviceIndex })
    }
  }

  function handleSaved(saved: Host) {
    applyLocally((hosts) => [...hosts.filter((h) => h.id !== saved.id), saved])
    showHost(saved.id)
  }

  function handleChanged(changed: Host) {
    applyLocally((hosts) => hosts.map((h) => (h.id === changed.id ? changed : h)))
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

  // On narrow screens one pane shows at a time: the tree, or the details/sessions.
  const showingPanel = selectedId !== null || activeTab !== DETAILS_TAB
  const menuHost = menu ? allHosts?.find((h) => h.id === menu.hostId) : undefined

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
            {admin ? (
              <Button size="sm" onClick={() => showHost(NEW_HOST)}>
                New host
              </Button>
            ) : null}
          </div>
        </div>

        {sessions.length > 0 && !showingPanel ? (
          <button type="button" className={styles.sessionsLink} onClick={() => setActiveTab(sessions[sessions.length - 1].id)}>
            {sessions.length} open {sessions.length === 1 ? 'session' : 'sessions'} ›
          </button>
        ) : null}

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
            <p className={styles.sidebarState}>{admin ? 'No hosts yet.' : 'No hosts have been shared with you yet.'}</p>
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
              onSelect={(id) => showHost(id)}
              onActivate={connectWithDefaults}
              onContextMenu={(hostId, x, y) => setMenu({ hostId, x, y })}
              menuHostId={menu?.hostId ?? null}
            />
          )}
        </div>
      </aside>

      <main className={styles.main}>
        {showingPanel ? (
          <button type="button" className={styles.back} onClick={() => showHost(null)}>
            ‹ All hosts
          </button>
        ) : null}

        {sessions.length > 0 ? (
          <div className={styles.tabs} role="tablist" aria-label="Open views">
            <button
              type="button"
              role="tab"
              className={styles.tab}
              aria-selected={activeTab === DETAILS_TAB}
              onClick={() => setActiveTab(DETAILS_TAB)}
            >
              <span className={styles.tabTitle}>{selectedHost?.name ?? 'Details'}</span>
              <span className={styles.tabDetail}>Details</span>
            </button>
            {sessions.map((session) => (
              <div key={session.id} className={`${styles.tabWrap} ${activeTab === session.id ? styles.tabWrapActive : ''}`}>
                <button
                  type="button"
                  role="tab"
                  className={styles.tab}
                  aria-selected={activeTab === session.id}
                  onClick={() => setActiveTab(session.id)}
                  onAuxClick={(event) => event.button === 1 && closeSession(session.id)}
                  title={`${session.title} — ${session.detail}`}
                >
                  <span className={styles.tabTitle}>{session.title}</span>
                  <span className={styles.tabDetail}>{session.detail}</span>
                </button>
                <button type="button" className={styles.tabClose} onClick={() => closeSession(session.id)} aria-label={`Close ${session.title}`}>
                  ×
                </button>
              </div>
            ))}
          </div>
        ) : null}

        <div className={styles.view} hidden={activeTab !== DETAILS_TAB}>
          {editing && (selectedId === NEW_HOST || selectedHost) ? (
            <HostEditor
              key={`${selectedId}:${cloneSource?.id ?? ''}`}
              host={selectedHost ?? null}
              cloneOf={cloneSource}
              knownTagNames={knownTagNames}
              knownTagValues={(name) => tagValues(allHosts ?? [], name)}
              onSaved={handleSaved}
              onCancel={() => update(selectedId === NEW_HOST ? { host: cloneSource?.id ?? null, clone: null } : { edit: null })}
            />
          ) : selectedHost ? (
            <HostDetails
              key={selectedHost.id}
              host={selectedHost}
              onEdit={() => update({ edit: '1' })}
              onClone={admin ? () => cloneHost(selectedHost) : undefined}
              onDeleted={handleDeleted}
              onTagClick={filterByTag}
              onConnectMenu={(anchor) => setMenu({ hostId: selectedHost.id, x: anchor.left, y: anchor.bottom + 4, connectOnly: true })}
              onChanged={handleChanged}
            />
          ) : selectedId && allHosts ? (
            <div className={styles.mainState}>
              <p className={styles.stateTitle}>Host not found</p>
              <p>It may have been deleted. Choose another host from the tree.</p>
            </div>
          ) : (
            <div className={styles.mainState}>
              {allHosts?.length === 0 && admin ? (
                <>
                  <p className={styles.stateTitle}>Your inventory is empty</p>
                  <p>Add your first host, with its services, credentials and tags.</p>
                  <Button onClick={() => showHost(NEW_HOST)}>New host</Button>
                </>
              ) : allHosts?.length === 0 ? (
                <>
                  <p className={styles.stateTitle}>No hosts yet</p>
                  <p>No hosts have been shared with you. Ask an administrator to give you access to the hosts or groups you need.</p>
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
        </div>

        <Suspense
          fallback={
            <div className={styles.mainState}>
              <Spinner label="Loading terminal" />
            </div>
          }
        >
          {sessions.map((session) => (
            session.protocol === 'web' ? (
              <WebSession
                key={session.id}
                session={session}
                active={activeTab === session.id}
                onShowHost={(id) => showHost(id)}
                onEnded={() => closeSession(session.id)}
              />
            ) : session.protocol === 'kvm' ? (
              <VirtualMachinesView
                key={session.id}
                session={session}
                active={activeTab === session.id}
                onShowHost={(id) => showHost(id)}
                onOpenConsole={(vm) => openConsole(session, vm)}
              />
            ) : session.protocol === 'spice' ? (
              <SpiceConsole key={session.id} session={session} active={activeTab === session.id} onShowHost={(id) => showHost(id)} />
            ) : session.protocol === 'vmvnc' ? (
              <VncConsole key={session.id} session={session} active={activeTab === session.id} onShowHost={(id) => showHost(id)} />
            ) : session.protocol === 'rdp' || session.protocol === 'vnc' ? (
              <DesktopSession
                key={session.id}
                session={session}
                active={activeTab === session.id}
                onShowHost={(id) => showHost(id)}
                onEnded={() => closeSession(session.id)}
              />
            ) : (
              <SshTerminal
                key={session.id}
                session={session}
                active={activeTab === session.id}
                onShowHost={(id) => showHost(id)}
                onEnded={() => closeSession(session.id)}
              />
            )
          ))}
        </Suspense>
      </main>

      {menu && menuHost ? (
        <ContextMenu
          label={`Actions for ${menuHost.name}`}
          entries={menu.connectOnly ? connectMenuEntries(menuHost, menuActions) : hostMenuEntries(menuHost, menuActions)}
          x={menu.x}
          y={menu.y}
          onClose={() => setMenu(null)}
        />
      ) : null}

      {prompt ? (
        <CredentialPromptDialog
          target={`${prompt.host.name} · ${serviceLabel(prompt.host.services[prompt.serviceIndex])}`}
          usernameOptional={prompt.host.services[prompt.serviceIndex].protocol === 'Vnc'}
          onConnect={(username, password) => {
            openSession(prompt.host, prompt.serviceIndex, undefined, { username, password })
            setPrompt(null)
          }}
          onCancel={() => setPrompt(null)}
        />
      ) : null}
    </div>
  )
}
