import type { MenuEntry } from '../../components/ui/ContextMenu'
import {
  formatAccount,
  PROTOCOL_INFO,
  primaryAddress,
  SUPPORTED_PROTOCOLS,
  type Host,
  type HostCredential,
  type HostService,
  type ServiceProtocol,
} from './types'

/** Protocols NKeeper can open sessions for; services of other types an older version stored aren't offered under Connect. */
const CONNECTABLE = new Set<ServiceProtocol>(SUPPORTED_PROTOCOLS)

/**
 * Web services open the host's own web interface; they need no credential from NKeeper. A Proxmox
 * service is also its node's web interface (its logins open VM consoles, from the VMs' menus).
 */
export const WEB_PROTOCOLS = new Set<ServiceProtocol>(['Http', 'Https', 'Proxmox'])

/** What an iDRAC service opens: its HTML5 virtual console, the server's status and power, or RACADM (its SSH command line). */
export type IdracMode = 'console' | 'overview' | 'racadm'

/** What a connection opens: an iDRAC's console, status or RACADM, or an SSH service's files (SFTP). */
export type ConnectMode = IdracMode | 'files'

/** Session types that can't be tunnelled through a jump host yet (guacd and RPC dial hosts themselves). */
const NOT_THROUGH_JUMP = new Set<ServiceProtocol>(['Rdp', 'Vnc', 'Rpc'])

export const IDRAC_MODES: Record<IdracMode, { label: string; detail: string }> = {
  console: { label: 'Virtual console', detail: 'HTML5' },
  overview: { label: 'Status & power', detail: 'Redfish' },
  racadm: { label: 'RACADM', detail: 'SSH' },
}

export interface HostMenuActions {
  /** Open a session with a saved credential. `mode` says what an iDRAC service opens. */
  connect: (host: Host, serviceIndex: number, credential: HostCredential, mode?: ConnectMode) => void
  /** Ask for a username and password, then open a session. A credential without a secret prefills its account. */
  connectWithPrompt: (host: Host, serviceIndex: number, account?: HostCredential, mode?: ConnectMode) => void
  /** Telnet: connect and type the login in the terminal, as the device asks for it. */
  connectWithoutLogin: (host: Host, serviceIndex: number) => void
  /** Open a web (HTTP/HTTPS) session, or an iDRAC's web interface; NKeeper connects to the host on the browser's behalf. */
  connectWeb: (host: Host, serviceIndex: number) => void
  /** RDP with the user's own domain login; absent when their sign-in has none to pass on. */
  signIn?: { account: string; connect: (host: Host, serviceIndex: number) => void }
  /** Open the host's web interface in a new browser tab, loaded directly by this computer. */
  openDirect: (host: Host, serviceIndex: number) => void
  edit: (host: Host) => void
  /** Tasks ▸ Scan ▸ Internal: log in over SSH with a credential (or ask for one when absent or without a secret). */
  scanInternal: (host: Host, serviceIndex: number, credential?: HostCredential) => void
  /** Tasks ▸ Scan ▸ External: probe the host over the network from the NKeeper server. */
  scanExternal: (host: Host) => void
  copyText: (text: string) => void
  /** Send a Wake-on-LAN packet (hosts with a MAC address). */
  wake?: (host: Host) => void
  /** A VM host's console and tasks, through the hypervisor it runs on (its Proxmox or KVM service and login). */
  guests?: GuestActions
}

/** How a VM host's console and tasks are reached: its Proxmox node's API, or its libvirt host over SSH. */
export type GuestVia = 'proxmox' | 'kvm'

export interface GuestActions {
  /** The host a VM runs on, when the user can see it. */
  hypervisorOf: (vm: Host) => Host | undefined
  /** Connect ▸ console, with a saved login, or asking for one (`ask`). */
  openConsole: (vm: Host, hypervisor: Host, serviceIndex: number, via: GuestVia, credential: HostCredential | undefined, ask: boolean) => void
  /** Tasks ▸ Proxmox / KVM: a power action or migration, with a saved login, or asking for one (`ask`). */
  runTask: (vm: Host, hypervisor: Host, serviceIndex: number, via: GuestVia, task: GuestTask, credential: HostCredential | undefined, ask: boolean) => void
}

/** What Tasks ▸ Proxmox (or KVM, without migrating) can do to a VM or container. */
export type GuestTask = 'migrate' | 'shutdown' | 'reboot' | 'pause' | 'hibernate' | 'stop' | 'reset' | 'resume' | 'start'

/** Each task's menu label, and whether it loses unsaved work (it asks with a red button). */
export const GUEST_TASKS: Record<GuestTask, { label: string; danger?: boolean }> = {
  migrate: { label: 'Migrate' },
  shutdown: { label: 'Shut down' },
  reboot: { label: 'Reboot' },
  pause: { label: 'Pause' },
  hibernate: { label: 'Hibernate' },
  stop: { label: 'Stop', danger: true },
  reset: { label: 'Reset', danger: true },
  resume: { label: 'Resume' },
  start: { label: 'Start' },
}

/**
 * A VM host's identity on its hypervisor, from what a scan recorded: the Proxmox VM ID
 * ("proxmox:<cluster>/<vmid>") or the libvirt UUID ("libvirt:<uuid>"); null for other hosts.
 */
export function guestId(host: Host): { via: GuestVia; id: string } | null {
  if (!host.isVirtualMachine) {
    return null
  }
  const proxmox = /^proxmox:[^/]*\/(\d+)$/.exec(host.virtualMachineId ?? '')
  if (proxmox) {
    return { via: 'proxmox', id: proxmox[1] }
  }
  const libvirt = /^libvirt:([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i.exec(host.virtualMachineId ?? '')
  return libvirt ? { via: 'kvm', id: libvirt[1] } : null
}

/**
 * The hypervisor a VM host runs on, with the service its console and tasks go through (Proxmox for a
 * Proxmox VM, KVM for a libvirt one); null when there's none to use.
 */
function guestHypervisorOf(host: Host, actions: Pick<HostMenuActions, 'guests'>): { node: Host; index: number; via: GuestVia; guests: GuestActions } | null {
  const guests = actions.guests
  const guest = guestId(host)
  const node = guests && guest ? guests.hypervisorOf(host) : undefined
  const index = node && guest ? node.services.findIndex((s) => s.protocol === (guest.via === 'proxmox' ? 'Proxmox' : 'Kvm')) : -1
  return guests && guest && node && index !== -1 ? { node, index, via: guest.via, guests } : null
}

/** Tasks ▸ Proxmox (or KVM) ▸ action ▸ the hypervisor's logins; none without one. */
function guestTaskEntries(host: Host, actions: Pick<HostMenuActions, 'guests'>): MenuEntry[] {
  const hypervisor = guestHypervisorOf(host, actions)
  if (!hypervisor) {
    return []
  }
  const { node, index, via, guests } = hypervisor
  const logins = (task: GuestTask) =>
    loginEntries(node, index, {
      connect: (_h, _i, credential) => guests.runTask(host, node, index, via, task, credential, false),
      connectWithPrompt: (_h, _i, account) => guests.runTask(host, node, index, via, task, account, true),
      connectWithoutLogin: () => {},
    })
  const entry = (task: GuestTask): MenuEntry => ({
    kind: 'item',
    label: task === 'migrate' ? 'Migrate…' : GUEST_TASKS[task].label,
    submenu: logins(task),
  })
  return [
    {
      kind: 'item',
      label: via === 'proxmox' ? 'Proxmox' : 'KVM',
      detail: node.name,
      submenu: [
        // libvirt migrations need SSH between the hosts, which NKeeper doesn't set up.
        ...(via === 'proxmox' ? [entry('migrate'), { kind: 'separator' } as const] : []),
        ...(['shutdown', 'reboot', 'pause', 'hibernate', 'stop', 'reset'] as const).map(entry),
        { kind: 'separator' },
        entry('resume'),
        entry('start'),
      ],
    },
  ]
}

/**
 * Proxmox console (or KVM console) ▸ the hypervisor's logins, for a VM a scan found on a hypervisor
 * that has the service for it; none otherwise.
 */
function guestConsoleEntries(host: Host, actions: Pick<HostMenuActions, 'guests'>): MenuEntry[] {
  const hypervisor = guestHypervisorOf(host, actions)
  if (!hypervisor) {
    return []
  }
  const { node, index, via, guests } = hypervisor
  // Logins are the hypervisor's: "connect" and "ask" open this VM's console through it.
  const logins = loginEntries(node, index, {
    connect: (_h, i, credential) => guests.openConsole(host, node, i, via, credential, false),
    connectWithPrompt: (_h, i, account) => guests.openConsole(host, node, i, via, account, true),
    connectWithoutLogin: () => {},
  })
  return [
    via === 'proxmox'
      ? { kind: 'item', label: 'Proxmox console', detail: `VNC · ${node.name}`, submenu: logins }
      : { kind: 'item', label: 'KVM console', detail: node.name, submenu: logins },
  ]
}

/** Files (SFTP) ▸ login, or ▸ SSH service ▸ login when there are several; none without an SSH service. */
function filesMenuEntries(host: Host, actions: Pick<HostMenuActions, 'connect' | 'connectWithPrompt' | 'connectWithoutLogin'>): MenuEntry[] {
  const ssh = host.services.map((service, index) => ({ service, index })).filter(({ service }) => service.protocol === 'Ssh')
  if (ssh.length === 0) {
    return []
  }
  return [
    {
      kind: 'item',
      label: 'Files',
      detail: 'SFTP',
      submenu:
        ssh.length === 1
          ? loginEntries(host, ssh[0].index, actions, 'files')
          : ssh.map(({ service, index }) => ({ kind: 'item', label: serviceLabel(service), detail: servicePort(service), submenu: loginEntries(host, index, actions, 'files') })),
    },
  ]
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
  // VNC servers usually ask for a password only; Telnet devices may too.
  return protocol !== 'Vnc' && protocol !== 'Telnet' && !credential.username ? 'No username' : null
}

/** A usable credential without a stored secret: connecting asks for the password (e.g. users found by a scan). */
export function credentialNeedsPassword(credential: HostCredential, protocol: ServiceProtocol): boolean {
  if (protocol === 'Telnet') {
    // The device asks in the terminal: whatever isn't stored, the user types there.
    return false
  }
  return protocol === 'Vnc' || protocol === 'Rdp' || protocol === 'Idrac' || protocol === 'Rpc' || protocol === 'Proxmox' ? !credential.hasPassword : !credential.hasPassword && !credential.hasPrivateKey
}

/** The host's right-click menu: Connect ▸ service ▸ credential, then host actions. */
export function hostMenuEntries(host: Host, actions: HostMenuActions): MenuEntry[] {
  return [
    { kind: 'header', label: host.name },
    { kind: 'item', label: 'Connect', submenu: connectMenuEntries(host, actions) },
    { kind: 'separator' },
    ...(host.canManage ? [{ kind: 'item', label: 'Edit', onSelect: () => actions.edit(host) } as const] : []),
    ...(host.canManage || (host.macAddress && actions.wake) || guestHypervisorOf(host, actions)
      ? [{ kind: 'item', label: 'Tasks', submenu: taskMenuEntries(host, actions) } as const]
      : []),
    {
      kind: 'item',
      label: 'Copy',
      submenu: [
        {
          kind: 'item',
          label: 'MAC',
          detail: host.macAddress ?? undefined,
          disabled: !host.macAddress,
          onSelect: () => host.macAddress && actions.copyText(host.macAddress),
        },
        {
          kind: 'item',
          label: 'Hostname',
          detail: host.hostname ?? undefined,
          disabled: !host.hostname,
          onSelect: () => host.hostname && actions.copyText(host.hostname),
        },
        {
          kind: 'item',
          label: 'IP',
          detail: host.ipAddress ?? undefined,
          disabled: !host.ipAddress,
          onSelect: () => host.ipAddress && actions.copyText(host.ipAddress),
        },
        {
          kind: 'item',
          label: 'Serial Number',
          detail: host.serialNumber ?? undefined,
          disabled: !host.serialNumber,
          onSelect: () => host.serialNumber && actions.copyText(host.serialNumber),
        },
      ],
    },
  ]
}

/** One entry per connectable service, each opening its credentials (also used by the Connect button). */
export function connectMenuEntries(
  host: Host,
  actions: Pick<HostMenuActions, 'connect' | 'connectWithPrompt' | 'connectWithoutLogin' | 'connectWeb' | 'openDirect' | 'signIn' | 'guests'>,
): MenuEntry[] {
  const services: MenuEntry[] = host.services.flatMap((service, index): MenuEntry | MenuEntry[] => {
    if (!CONNECTABLE.has(service.protocol)) {
      return []
    }

    if (host.jumpHostId && NOT_THROUGH_JUMP.has(service.protocol)) {
      return { kind: 'item', label: serviceLabel(service), detail: 'Not through a jump host yet', disabled: true }
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

  const console = guestConsoleEntries(host, actions)
  const entries =
    services.length > 0 || console.length > 0
      ? services
      : [{ kind: 'item', label: host.services.length > 0 ? 'No services to connect with' : 'No services', detail: 'Edit the host to add one', disabled: true } as const]
  return [...entries, ...console, ...filesMenuEntries(host, actions)]
}

/**
 * The host's credentials usable with a service (asking for a password when none is stored), the
 * user's own domain login for RDP, then "Enter credentials…".
 */
function loginEntries(
  host: Host,
  index: number,
  actions: Pick<HostMenuActions, 'connect' | 'connectWithPrompt' | 'connectWithoutLogin' | 'signIn'>,
  mode?: ConnectMode,
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
  const signIn = protocol === 'Rdp' && actions.signIn ? actions.signIn : null
  return [
    ...credentials,
    ...(credentials.length > 0 ? [{ kind: 'separator' } as const] : []),
    ...(signIn ? [{ kind: 'item', label: 'My sign-in', detail: signIn.account, onSelect: () => signIn.connect(host, index) } as const] : []),
    ...(protocol === 'Telnet'
      ? [{ kind: 'item', label: 'Without a login', detail: 'Type it in the terminal', onSelect: () => actions.connectWithoutLogin(host, index) } as const]
      : []),
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
export function taskMenuEntries(host: Host, actions: Pick<HostMenuActions, 'scanInternal' | 'scanExternal' | 'wake' | 'guests'>): MenuEntry[] {
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
    ...(host.macAddress && actions.wake
      ? [{ kind: 'item', label: 'Wake', detail: 'Wake-on-LAN', onSelect: () => actions.wake!(host) } as const]
      : []),
    ...guestTaskEntries(host, actions),
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
  // A Proxmox node's web interface is on 8006 unless the service says otherwise.
  const port = service.port ?? PROTOCOL_INFO[service.protocol].defaultPort
  return `${scheme}://${hostPart}${port && port !== defaultPort ? `:${port}` : ''}/`
}
