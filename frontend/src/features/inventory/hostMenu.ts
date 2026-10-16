import type { MenuEntry } from '../../components/ui/ContextMenu'
import { formatAccount, PROTOCOL_INFO, primaryAddress, type Host, type HostCredential, type HostService, type ServiceProtocol } from './types'

/** Protocols NKeeper can open sessions for. Other services (SSMS, Telnet, FTP…) are stored but not offered under Connect. */
const CONNECTABLE = new Set<ServiceProtocol>(['Ssh', 'Rdp', 'Rpc', 'Vnc', 'Http', 'Https', 'Kvm', 'Idrac'])

/** Web services open the host's own web interface; they need no credential from NKeeper. */
export const WEB_PROTOCOLS = new Set<ServiceProtocol>(['Http', 'Https'])

/** What an iDRAC service opens: its HTML5 virtual console, the server's status and power, or RACADM (its SSH command line). */
export type IdracMode = 'console' | 'overview' | 'racadm'

export const IDRAC_MODES: Record<IdracMode, { label: string; detail: string }> = {
  console: { label: 'Virtual console', detail: 'HTML5' },
  overview: { label: 'Status & power', detail: 'Redfish' },
  racadm: { label: 'RACADM', detail: 'SSH' },
}

export interface HostMenuActions {
  /** Open a session with a saved credential. `mode` says what an iDRAC service opens. */
  connect: (host: Host, serviceIndex: number, credential: HostCredential, mode?: IdracMode) => void
  /** Ask for a username and password, then open a session. A credential without a secret prefills its account. */
  connectWithPrompt: (host: Host, serviceIndex: number, account?: HostCredential, mode?: IdracMode) => void
  /** Open a web (HTTP/HTTPS) session, or an iDRAC's web interface; NKeeper connects to the host on the browser's behalf. */
  connectWeb: (host: Host, serviceIndex: number) => void
  /** Open the host's web interface in a new browser tab, loaded directly by this computer. */
  openDirect: (host: Host, serviceIndex: number) => void
  edit: (host: Host) => void
  /** Start a new host from a copy of this one; absent when the user can't add hosts. */
  clone?: (host: Host) => void
  /** Tasks ▸ Scan ▸ Internal: log in over SSH with a credential (or ask for one when absent or without a secret). */
  scanInternal: (host: Host, serviceIndex: number, credential?: HostCredential) => void
  /** Tasks ▸ Scan ▸ External: probe the host over the network from the NKeeper server. */
  scanExternal: (host: Host) => void
  copyAddress: (host: Host) => void
}

export function serviceLabel(service: HostService): string {
  const info = PROTOCOL_INFO[service.protocol]
  return service.label ? `${info.label} · ${service.label}` : info.label
}

function servicePort(service: HostService): string {
  return `:${service.port ?? PROTOCOL_INFO[service.protocol].defaultPort ?? ''}`
}

/** An iDRAC is shown at its own address. */
function serviceDetail(service: HostService): string {
  return service.protocol === 'Idrac' && service.address ? service.address : servicePort(service)
}

/** Why a credential cannot be used with a protocol at all, or null when it can. */
export function credentialProblem(credential: HostCredential, protocol: ServiceProtocol): string | null {
  // VNC servers usually ask for a password only.
  return protocol !== 'Vnc' && !credential.username ? 'No username' : null
}

/** A usable credential without a stored secret: connecting asks for the password (e.g. users found by a scan). */
export function credentialNeedsPassword(credential: HostCredential, protocol: ServiceProtocol): boolean {
  return protocol === 'Vnc' || protocol === 'Rdp' || protocol === 'Idrac' || protocol === 'Rpc' ? !credential.hasPassword : !credential.hasPassword && !credential.hasPrivateKey
}

/** The host's right-click menu: Connect ▸ service ▸ credential, then host actions. */
export function hostMenuEntries(host: Host, actions: HostMenuActions): MenuEntry[] {
  return [
    { kind: 'header', label: host.name },
    { kind: 'item', label: 'Connect', submenu: connectMenuEntries(host, actions) },
    { kind: 'separator' },
    ...(host.canManage ? [{ kind: 'item', label: 'Edit', onSelect: () => actions.edit(host) } as const] : []),
    ...(actions.clone ? [{ kind: 'item', label: 'Clone', detail: 'New host from this one', onSelect: () => actions.clone!(host) } as const] : []),
    ...(host.canManage ? [{ kind: 'item', label: 'Tasks', submenu: taskMenuEntries(host, actions) } as const] : []),
    { kind: 'item', label: 'Copy address', detail: primaryAddress(host), onSelect: () => actions.copyAddress(host) },
  ]
}

/** One entry per connectable service, each opening its credentials (also used by the Connect button). */
export function connectMenuEntries(
  host: Host,
  actions: Pick<HostMenuActions, 'connect' | 'connectWithPrompt' | 'connectWeb' | 'openDirect'>,
): MenuEntry[] {
  const services: MenuEntry[] = host.services.flatMap((service, index): MenuEntry | MenuEntry[] => {
    if (!CONNECTABLE.has(service.protocol)) {
      return []
    }

    const web: MenuEntry[] = [
      { kind: 'item', label: 'Through NKeeper', detail: 'Server connects', onSelect: () => actions.connectWeb(host, index) },
      { kind: 'item', label: 'Directly in a new tab', detail: 'This computer connects', onSelect: () => actions.openDirect(host, index) },
    ]

    if (WEB_PROTOCOLS.has(service.protocol)) {
      return { kind: 'item', label: serviceLabel(service), detail: servicePort(service), submenu: web }
    }

    if (service.protocol === 'Idrac') {
      return {
        kind: 'item',
        label: serviceLabel(service),
        detail: serviceDetail(service),
        submenu: [
          ...(Object.keys(IDRAC_MODES) as IdracMode[]).map((mode): MenuEntry => ({
            kind: 'item',
            label: IDRAC_MODES[mode].label,
            detail: IDRAC_MODES[mode].detail,
            submenu: loginEntries(host, index, actions, mode),
          })),
          { kind: 'separator' },
          { kind: 'item', label: 'Web interface', submenu: web },
        ],
      }
    }

    return { kind: 'item', label: serviceLabel(service), detail: servicePort(service), submenu: loginEntries(host, index, actions) }
  })

  return services.length > 0
    ? services
    : [{ kind: 'item', label: host.services.length > 0 ? 'No services to connect with' : 'No services', detail: 'Edit the host to add one', disabled: true }]
}

/** The host's credentials usable with a service (asking for a password when none is stored), then "Enter credentials…". */
function loginEntries(
  host: Host,
  index: number,
  actions: Pick<HostMenuActions, 'connect' | 'connectWithPrompt'>,
  mode?: IdracMode,
): MenuEntry[] {
  const protocol = host.services[index].protocol
  const credentials = host.credentials.map((credential): MenuEntry => {
    const problem = credentialProblem(credential, protocol)
    const asks = credentialNeedsPassword(credential, protocol)
    return {
      ...credentialEntry(credential, problem ?? (asks ? 'Asks for password' : undefined)),
      disabled: problem !== null,
      onSelect: () => (asks ? actions.connectWithPrompt(host, index, credential, mode) : actions.connect(host, index, credential, mode)),
    }
  })
  return [
    ...credentials,
    ...(credentials.length > 0 ? [{ kind: 'separator' } as const] : []),
    { kind: 'item', label: 'Enter credentials…', onSelect: () => actions.connectWithPrompt(host, index, undefined, mode) },
  ]
}

/** A credential as a menu item: its label or account, with `detail` (or the account, or "SSH key") beside it. */
function credentialEntry(credential: HostCredential, detail?: string): Extract<MenuEntry, { kind: 'item' }> {
  const account = formatAccount(credential)
  return {
    kind: 'item',
    label: credential.label ?? account ?? 'Credential',
    detail: detail ?? (credential.label ? (account ?? undefined) : credential.hasPrivateKey ? 'SSH key' : undefined),
  }
}

/** Tasks ▸ Scan ▸ Internal (SSH or RPC service ▸ credential) or External. */
export function taskMenuEntries(host: Host, actions: Pick<HostMenuActions, 'scanInternal' | 'scanExternal'>): MenuEntry[] {
  const ssh = host.services.map((service, index) => ({ service, index })).filter(({ service }) => service.protocol === 'Ssh' || service.protocol === 'Rpc')
  const logins = (index: number): MenuEntry[] => [
    ...host.credentials
      .filter((c) => credentialProblem(c, host.services[index].protocol) === null)
      .map((credential): MenuEntry => ({
        ...credentialEntry(credential, credentialNeedsPassword(credential, host.services[index].protocol) ? 'Asks for password' : undefined),
        onSelect: () => actions.scanInternal(host, index, credential),
      })),
    { kind: 'separator' },
    { kind: 'item', label: 'Enter credentials…', onSelect: () => actions.scanInternal(host, index) },
  ]
  const internal: MenuEntry =
    ssh.length === 0
      ? { kind: 'item', label: 'Internal', detail: 'Needs an SSH or RPC service', disabled: true }
      : {
          kind: 'item',
          label: 'Internal',
          detail: ssh.every(({ service }) => service.protocol === 'Rpc') ? 'Over RPC (WMI)' : 'Over SSH',
          // One SSH service: its logins directly; several: pick the service first.
          submenu:
            ssh.length === 1
              ? logins(ssh[0].index)
              : ssh.map(({ service, index }) => ({ kind: 'item', label: serviceLabel(service), detail: servicePort(service), submenu: logins(index) })),
        }
  return [
    {
      kind: 'item',
      label: 'Scan',
      submenu: [internal, { kind: 'item', label: 'External', detail: 'Over the network', onSelect: () => actions.scanExternal(host) }],
    },
  ]
}

/** The first connectable service with the first usable credential, for double-click connect. */
export function defaultConnection(host: Host): { serviceIndex: number; credential: HostCredential | null } | null {
  const serviceIndex = host.services.findIndex((s) => CONNECTABLE.has(s.protocol))
  if (serviceIndex === -1) {
    return null
  }
  const protocol = host.services[serviceIndex].protocol
  const usable = host.credentials.filter((c) => credentialProblem(c, protocol) === null)
  // Prefer a credential with a stored secret; one without asks for its password.
  return { serviceIndex, credential: usable.find((c) => !credentialNeedsPassword(c, protocol)) ?? usable[0] ?? null }
}

/** The host's own URL for a web service, as this computer would open it. */
export function directWebUrl(host: Host, service: HostService): string {
  // An iDRAC's web interface is HTTPS at its own address.
  const scheme = service.protocol === 'Http' ? 'http' : 'https'
  const address = service.address ?? primaryAddress(host)
  const hostPart = address.includes(':') ? `[${address}]` : address
  const defaultPort = scheme === 'https' ? 443 : 80
  return `${scheme}://${hostPart}${service.port && service.port !== defaultPort ? `:${service.port}` : ''}/`
}
