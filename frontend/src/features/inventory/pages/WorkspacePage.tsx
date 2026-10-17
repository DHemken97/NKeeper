import { lazy, Suspense, useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { ContextMenu } from '../../../components/ui/ContextMenu'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { healthApi, hostsApi } from '../api/inventoryApi'
import { BulkEditPanel } from '../components/BulkEditPanel'
import { GroupByPicker } from '../components/GroupByPicker'
import { HostDetails } from '../components/HostDetails'
import { HostEditor } from '../components/HostEditor'
import { HostTree, type SelectMode } from '../components/HostTree'
import { ImportHostsDialog } from '../components/ImportHostsDialog'
import { ScanDialog } from '../components/ScanDialog'
import {
  buildTree,
  decodeGroupKey,
  encodeGroupKey,
  sortHosts,
  tagNames,
  tagValues,
  type GroupKey,
  type HostSort,
  type TreeGroup,
  type TreeNode,
} from '../grouping'
import { CredentialPromptDialog } from '../../terminal/components/CredentialPromptDialog'
import type { VirtualMachine } from '../../terminal/kvm'
import type { RemoteSession } from '../../terminal/types'
import { useResource } from '../hooks/useResource'
import {
  connectMenuEntries,
  credentialNeedsPassword,
  defaultConnection,
  directWebUrl,
  hostMenuEntries,
  IDRAC_MODES,
  serviceLabel,
  WEB_PROTOCOLS,
  type HostMenuActions,
  type ConnectMode,
  type IdracMode,
} from '../hostMenu'
import { matchesSearch, parseSearch, tagSearchTerm } from '../search'
import {
  formatAccount,
  hostHealth,
  primaryAddress,
  type Host,
  type HostCredential,
  type HostTag,
  type InternalScanLogin,
  type ServiceCheck,
  type ServiceProtocol,
} from '../types'
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
const FileBrowser = lazy(() => import('../../terminal/components/FileBrowser').then((m) => ({ default: m.FileBrowser })))
const IdracView = lazy(() => import('../../terminal/components/IdracView').then((m) => ({ default: m.IdracView })))
const RpcSession = lazy(() => import('../../terminal/components/RpcSession').then((m) => ({ default: m.RpcSession })))
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

/** What a session on a service opens: for an iDRAC, one of its modes or its web interface. */
type OpenMode = ConnectMode | 'web'

/** Which session view a service opens in. An iDRAC opens its console unless asked for something else. */
function sessionProtocol(protocol: ServiceProtocol, mode?: OpenMode): RemoteSession['protocol'] {
  if (WEB_PROTOCOLS.has(protocol) || mode === 'web') {
    return 'web'
  }
  if (mode === 'files') {
    return 'sftp'
  }
  if (protocol === 'Idrac') {
    // RACADM is the iDRAC's SSH command line.
    return mode === 'overview' ? 'idrac' : mode === 'racadm' ? 'ssh' : 'idracconsole'
  }
  return protocol === 'Telnet' ? 'telnet' : protocol === 'Rdp' ? 'rdp' : protocol === 'Vnc' ? 'vnc' : protocol === 'Kvm' ? 'kvm' : protocol === 'Rpc' ? 'rpc' : 'ssh'
}

/** Host ids in the order the tree shows them (skipping collapsed groups), each once. */
function visibleHostOrder(nodes: TreeNode[], collapsed: ReadonlySet<string>, into: string[] = []): string[] {
  for (const node of nodes) {
    if (node.type === 'host') {
      if (!into.includes(node.host.id)) {
        into.push(node.host.id)
      }
    } else if (!collapsed.has(node.id)) {
      visibleHostOrder(node.children, collapsed, into)
    }
  }
  return into
}

function findGroup(nodes: TreeNode[], id: string): TreeGroup | undefined {
  for (const node of nodes) {
    if (node.type === 'group') {
      const found = node.id === id ? node : findGroup(node.children, id)
      if (found) {
        return found
      }
    }
  }
  return undefined
}

/** Every group's id, nested ones included. */
function allGroupIds(nodes: TreeNode[]): string[] {
  return nodes.flatMap((node) => (node.type === 'group' ? [node.id, ...allGroupIds(node.children)] : []))
}

/** Every host under a group, collapsed or not. */
function groupHostIds(group: TreeGroup): string[] {
  return [...new Set(group.children.flatMap((child) => (child.type === 'host' ? [child.host.id] : groupHostIds(child))))]
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
  // Service health, refreshed every minute (the server checks every few minutes).
  const health = useResource((signal) => healthApi.list(signal))
  const reloadHealth = health.reload
  useEffect(() => {
    const timer = setInterval(reloadHealth, 60_000)
    return () => clearInterval(timer)
  }, [reloadHealth])
  const checksByHost = useMemo(() => {
    const byHost = new Map<string, ServiceCheck[]>()
    for (const check of health.data ?? []) {
      byHost.set(check.hostId, [...(byHost.get(check.hostId) ?? []), check])
    }
    return byHost
  }, [health.data])
  const healthByHost = useMemo(
    () => new Map([...checksByHost].flatMap(([id, checks]) => (hostHealth(checks) ? [[id, hostHealth(checks)!] as const] : []))),
    [checksByHost],
  )
  const [wakeNotice, setWakeNotice] = useState<{ text: string; error: boolean } | null>(null)
  const [local, setLocal] = useState<LocalChanges>({ basedOn: undefined, hosts: [] })
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set())
  const [sessions, setSessions] = useState<RemoteSession[]>([])
  const [activeTab, setActiveTab] = useState(DETAILS_TAB)
  const [menu, setMenu] = useState<MenuState | null>(null)
  /** Asking for a login: to connect, or for an internal scan. `account` prefills a credential without a secret. */
  const [prompt, setPrompt] = useState<{
    host: Host
    serviceIndex: number
    account?: HostCredential
    purpose: 'connect' | 'scan'
    mode?: ConnectMode
  } | null>(null)
  const [scan, setScan] = useState<{ host: Host; kind: 'internal' | 'external'; login?: InternalScanLogin } | null>(null)
  const [importing, setImporting] = useState(false)
  /** Hosts selected together for bulk changes; two or more replace the details with the bulk panel. */
  const [bulk, setBulk] = useState<ReadonlySet<string>>(new Set())
  /** Where a Shift+click range starts. */
  const [anchor, setAnchor] = useState<string | null>(null)
  const [groupMenu, setGroupMenu] = useState<{ groupId: string; x: number; y: number } | null>(null)

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
  const tree = useMemo(() => buildTree(visibleHosts, groupKeys, allHosts ?? []), [visibleHosts, groupKeys, allHosts])
  const knownTagNames = useMemo(() => tagNames(allHosts ?? []), [allHosts])
  const groupIds = useMemo(() => allGroupIds(tree), [tree])
  const anyExpanded = groupIds.some((id) => !collapsed.has(id))
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

  /** The bulk selection becomes `ids`; one host (or none) is an ordinary selection instead. */
  function selectMany(ids: ReadonlySet<string>) {
    setActiveTab(DETAILS_TAB)
    if (ids.size > 1) {
      setBulk(ids)
      return
    }
    setBulk(new Set())
    const [only] = ids
    showHost(only ?? null)
  }

  /** A click in the tree: open one host, or add to / extend the bulk selection. */
  function selectHost(id: string, mode: SelectMode) {
    const current = bulk.size > 0 ? bulk : new Set(selectedHost ? [selectedHost.id] : [])
    if (mode === 'single' || (mode === 'range' && !(anchor ?? selectedHost?.id))) {
      setAnchor(id)
      selectMany(new Set([id]))
      return
    }
    if (mode === 'toggle') {
      const next = new Set(current)
      if (!next.delete(id)) {
        next.add(id)
      }
      setAnchor(id)
      selectMany(next)
      return
    }
    const order = visibleHostOrder(tree, collapsed)
    const from = order.indexOf((anchor ?? selectedHost?.id)!)
    const to = order.indexOf(id)
    const range = from === -1 || to === -1 ? [id] : order.slice(Math.min(from, to), Math.max(from, to) + 1)
    selectMany(new Set([...current, ...range]))
  }

  /** Opens the editor for a new host prefilled from this one. */
  function cloneHost(host: Host) {
    update({ host: NEW_HOST, edit: null, clone: host.id })
    setActiveTab(DETAILS_TAB)
  }

  function openSession(
    host: Host,
    serviceIndex: number,
    credential?: HostCredential,
    oneOff?: { username: string; password: string; domain?: string },
    mode?: OpenMode,
    useSignIn?: boolean,
  ) {
    const id = `session-${nextSessionId++}`
    const account = credential ? (formatAccount(credential) ?? credential.label) : useSignIn ? user?.signInAccount : oneOff?.username
    const sameHost = sessions.filter((s) => s.hostId === host.id).length
    const service = host.services[serviceIndex]
    const modeLabel =
      mode === 'files' ? 'Files' : service.protocol !== 'Idrac' ? null : mode === 'web' ? 'Web interface' : IDRAC_MODES[(mode as IdracMode | undefined) ?? 'console'].label
    setSessions((current) => [
      ...current,
      {
        id,
        hostId: host.id,
        protocol: sessionProtocol(service.protocol, mode),
        title: sameHost > 0 ? `${host.name} (${sameHost + 1})` : host.name,
        detail: [serviceLabel(service), modeLabel, account].filter(Boolean).join(' · '),
        request: { hostId: host.id, serviceIndex, credentialId: credential?.id, oneOff, ...(useSignIn ? { useSignIn } : {}) },
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

  /** Opens an iDRAC's console, RACADM or web interface from its status tab, with the same login. */
  function openFromIdrac(source: RemoteSession, mode: OpenMode) {
    const host = allHosts?.find((h) => h.id === source.hostId)
    if (!host) {
      return
    }
    const credential = host.credentials.find((c) => c.id === source.request.credentialId)
    openSession(host, source.request.serviceIndex, credential, source.request.oneOff, mode)
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
    connect: (host, serviceIndex, credential, mode) => openSession(host, serviceIndex, credential, undefined, mode),
    connectWithPrompt: (host, serviceIndex, account, mode) => setPrompt({ host, serviceIndex, account, purpose: 'connect', mode }),
    connectWithoutLogin: (host, serviceIndex) => openSession(host, serviceIndex),
    connectWeb: (host, serviceIndex) => openSession(host, serviceIndex, undefined, undefined, 'web'),
    signIn: user?.signInAccount
      ? { account: user.signInAccount, connect: (host, serviceIndex) => openSession(host, serviceIndex, undefined, undefined, undefined, true) }
      : undefined,
    // Called from the menu click itself, so the browser allows the new tab.
    openDirect: (host, serviceIndex) => void window.open(directWebUrl(host, host.services[serviceIndex]), '_blank', 'noopener,noreferrer'),
    edit: (host) => showHost(host.id, true),
    clone: admin ? cloneHost : undefined,
    copyAddress: (host) => void navigator.clipboard?.writeText(primaryAddress(host)),
    wake: (host) =>
      void hostsApi.wake(host.id).then(
        () => setWakeNotice({ text: `Wake-on-LAN sent to ${host.name} (${host.macAddress}).`, error: false }),
        (err: unknown) => setWakeNotice({ text: toFormErrors(err).message ?? 'The Wake-on-LAN packet couldn’t be sent.', error: true }),
      ),
    scanInternal: (host, serviceIndex, credential) =>
      credential && !credentialNeedsPassword(credential, host.services[serviceIndex].protocol)
        ? setScan({ host, kind: 'internal', login: { serviceIndex, credentialId: credential.id } })
        : setPrompt({ host, serviceIndex, account: credential, purpose: 'scan' }),
    scanExternal: (host) => setScan({ host, kind: 'external' }),
  }

  /** Double-click: connect with the first SSH service and credential, or ask for one. */
  function connectWithDefaults(hostId: string) {
    const host = allHosts?.find((h) => h.id === hostId)
    const connection = host ? defaultConnection(host) : null
    if (!host || !connection) {
      showHost(hostId)
    } else if (WEB_PROTOCOLS.has(host.services[connection.serviceIndex].protocol)) {
      openSession(host, connection.serviceIndex)
    } else if (connection.credential && !credentialNeedsPassword(connection.credential, host.services[connection.serviceIndex].protocol)) {
      openSession(host, connection.serviceIndex, connection.credential)
    } else if (!connection.credential && host.services[connection.serviceIndex].protocol === 'Rdp' && user?.signInAccount) {
      // No saved login for this desktop: the user's own domain sign-in rather than a prompt.
      openSession(host, connection.serviceIndex, undefined, undefined, undefined, true)
    } else if (host.services[connection.serviceIndex].protocol === 'Telnet') {
      // No saved login: the device asks for it in the terminal.
      openSession(host, connection.serviceIndex)
    } else {
      setPrompt({ host, serviceIndex: connection.serviceIndex, account: connection.credential ?? undefined, purpose: 'connect' })
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
  const bulkHosts = bulk.size > 1 ? (allHosts ?? []).filter((h) => bulk.has(h.id)) : []
  const showingPanel = selectedId !== null || activeTab !== DETAILS_TAB || bulkHosts.length > 1
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
            {groupIds.length > 0 ? (
              <button
                type="button"
                className={styles.iconButton}
                onClick={() => setCollapsed(anyExpanded ? new Set(groupIds) : new Set())}
                aria-label={anyExpanded ? 'Collapse all groups' : 'Expand all groups'}
                title={anyExpanded ? 'Collapse all' : 'Expand all'}
              >
                <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">
                  <path
                    d={anyExpanded ? 'M4.5 7 8 3.5 11.5 7M4.5 12.5 8 9l3.5 3.5' : 'M4.5 3.5 8 7l3.5-3.5M4.5 9 8 12.5 11.5 9'}
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="1.5"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  />
                </svg>
              </button>
            ) : null}
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
              <>
                <Button size="sm" variant="secondary" onClick={() => setImporting(true)}>
                  Import
                </Button>
                <Button size="sm" onClick={() => showHost(NEW_HOST)}>
                  New host
                </Button>
              </>
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
              bulkIds={bulkHosts.length > 1 ? bulk : new Set()}
              collapsed={collapsed}
              onToggle={toggleGroup}
              onSelect={selectHost}
              onActivate={connectWithDefaults}
              onContextMenu={(hostId, x, y) => setMenu({ hostId, x, y })}
              onGroupContextMenu={(groupId, x, y) => setGroupMenu({ groupId, x, y })}
              menuHostId={menu?.hostId ?? null}
              health={healthByHost}
            />
          )}
        </div>
      </aside>

      <main className={styles.main}>
        {wakeNotice ? (
          <div className={styles.notice}>
            <Alert variant={wakeNotice.error ? 'error' : 'info'}>{wakeNotice.text}</Alert>
            <Button variant="ghost" size="sm" onClick={() => setWakeNotice(null)}>
              Dismiss
            </Button>
          </div>
        ) : null}
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
          {bulkHosts.length > 1 ? (
            <BulkEditPanel
              key={[...bulk].sort().join(',')}
              selected={bulkHosts}
              hiddenCount={bulkHosts.filter((h) => !visibleHosts.includes(h)).length}
              allHosts={allHosts ?? []}
              knownTagNames={knownTagNames}
              knownTagValues={(name) => tagValues(allHosts ?? [], name)}
              onApplied={(updated) => {
                const byId = new Map(updated.map((h) => [h.id, h]))
                applyLocally((hosts) => hosts.map((h) => byId.get(h.id) ?? h))
              }}
              onClear={() => selectMany(new Set())}
              canAddHosts={admin}
              onScanned={(changed) => applyLocally((hosts) => [...hosts.filter((h) => h.id !== changed.id), changed])}
            />
          ) : editing && (selectedId === NEW_HOST || selectedHost) ? (
            <HostEditor
              key={`${selectedId}:${cloneSource?.id ?? ''}`}
              host={selectedHost ?? null}
              cloneOf={cloneSource}
              knownTagNames={knownTagNames}
              knownTagValues={(name) => tagValues(allHosts ?? [], name)}
              allHosts={allHosts ?? []}
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
              allHosts={allHosts ?? []}
              onShowHost={(id) => showHost(id)}
              checks={checksByHost.get(selectedHost.id)}
              onHealthChanged={reloadHealth}
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
            session.protocol === 'web' || session.protocol === 'idracconsole' ? (
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
            ) : session.protocol === 'sftp' ? (
              <FileBrowser key={session.id} session={session} active={activeTab === session.id} onShowHost={(id) => showHost(id)} />
            ) : session.protocol === 'idrac' ? (
              <IdracView
                key={session.id}
                session={session}
                active={activeTab === session.id}
                onShowHost={(id) => showHost(id)}
                onOpen={(mode) => openFromIdrac(session, mode)}
                host={allHosts?.find((h) => h.id === session.hostId)}
                onHostChanged={handleChanged}
              />
            ) : session.protocol === 'spice' ? (
              <SpiceConsole key={session.id} session={session} active={activeTab === session.id} onShowHost={(id) => showHost(id)} />
            ) : session.protocol === 'vmvnc' ? (
              <VncConsole key={session.id} session={session} active={activeTab === session.id} onShowHost={(id) => showHost(id)} />
            ) : session.protocol === 'rpc' ? (
              <RpcSession key={session.id} session={session} active={activeTab === session.id} onShowHost={(id) => showHost(id)} />
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

      {groupMenu ? <GroupMenu tree={tree} state={groupMenu} bulk={bulk} selectedHost={selectedHost} onSelect={selectMany} onClose={() => setGroupMenu(null)} /> : null}

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
          usernameOptional={['Vnc', 'Telnet'].includes(prompt.host.services[prompt.serviceIndex].protocol)}
          initialUsername={prompt.account?.username ?? undefined}
          submitLabel={prompt.purpose === 'scan' ? 'Scan' : undefined}
          onConnect={(username, password) => {
            const domain = prompt.account?.domain ?? undefined
            if (prompt.purpose === 'scan') {
              setScan({ host: prompt.host, kind: 'internal', login: { serviceIndex: prompt.serviceIndex, username, password, ...(domain ? { domain } : {}) } })
            } else {
              openSession(prompt.host, prompt.serviceIndex, undefined, { username, password, ...(domain ? { domain } : {}) }, prompt.mode)
            }
            setPrompt(null)
          }}
          onCancel={() => setPrompt(null)}
        />
      ) : null}
      {scan ? (
        <ScanDialog
          host={scan.host}
          kind={scan.kind}
          login={scan.login}
          canAddHosts={admin}
          onClose={() => setScan(null)}
          // A scan can also add hosts (VMs found on a hypervisor), so new ones join the tree.
          onApplied={(changed) => applyLocally((hosts) => [...hosts.filter((h) => h.id !== changed.id), changed])}
        />
      ) : null}
      {importing ? <ImportHostsDialog onClose={() => setImporting(false)} onImported={resource.reload} /> : null}
    </div>
  )
}

interface GroupMenuProps {
  tree: TreeNode[]
  state: { groupId: string; x: number; y: number }
  bulk: ReadonlySet<string>
  selectedHost: Host | undefined
  onSelect: (ids: ReadonlySet<string>) => void
  onClose: () => void
}

/** A group's menu: select its hosts for bulk changes, or add them to the selection. */
function GroupMenu({ tree, state, bulk, selectedHost, onSelect, onClose }: GroupMenuProps) {
  const group = findGroup(tree, state.groupId)
  if (!group) {
    return null
  }
  const ids = groupHostIds(group)
  const current = bulk.size > 0 ? bulk : new Set(selectedHost ? [selectedHost.id] : [])
  const hosts = (n: number) => (n === 1 ? '1 host' : `${n} hosts`)
  return (
    <ContextMenu
      label={`Actions for ${group.label}`}
      entries={[
        { kind: 'header', label: group.label },
        { kind: 'item', label: `Select ${hosts(ids.length)}`, detail: 'For bulk changes', disabled: ids.length === 0, onSelect: () => onSelect(new Set(ids)) },
        {
          kind: 'item',
          label: 'Add to selection',
          detail: hosts(ids.length),
          disabled: current.size === 0 || ids.every((id) => current.has(id)),
          onSelect: () => onSelect(new Set([...current, ...ids])),
        },
      ]}
      x={state.x}
      y={state.y}
      onClose={onClose}
    />
  )
}
