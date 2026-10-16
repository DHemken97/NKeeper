import type { MenuEntry } from '../../components/ui/ContextMenu'
import { formatAccount, PROTOCOL_INFO, primaryAddress, type Host, type HostCredential, type HostService, type ServiceProtocol } from './types'

/** Protocols NKeeper can open sessions for today. */
const CONNECTABLE = new Set<ServiceProtocol>(['Ssh', 'Rdp', 'Http', 'Https'])

/** Web services open the host's own web interface; they need no credential from NKeeper. */
export const WEB_PROTOCOLS = new Set<ServiceProtocol>(['Http', 'Https'])

export interface HostMenuActions {
  /** Open a session with a saved credential. */
  connect: (host: Host, serviceIndex: number, credential: HostCredential) => void
  /** Ask for a username and password, then open a session. */
  connectWithPrompt: (host: Host, serviceIndex: number) => void
  /** Open a web (HTTP/HTTPS) session; NKeeper connects to the host on the browser's behalf. */
  connectWeb: (host: Host, serviceIndex: number) => void
  edit: (host: Host) => void
  copyAddress: (host: Host) => void
}

export function serviceLabel(service: HostService): string {
  const info = PROTOCOL_INFO[service.protocol]
  return service.label ? `${info.label} · ${service.label}` : info.label
}

function servicePort(service: HostService): string {
  return `:${service.port ?? PROTOCOL_INFO[service.protocol].defaultPort ?? ''}`
}

/** Why a credential cannot be used with a protocol, or null when it can. */
export function credentialProblem(credential: HostCredential, protocol: ServiceProtocol): string | null {
  if (!credential.username) {
    return 'No username'
  }
  if (protocol === 'Rdp') {
    return credential.hasPassword ? null : 'No password'
  }
  if (!credential.hasPassword && !credential.hasPrivateKey) {
    return 'No password or key'
  }
  return null
}

/** The host's right-click menu: Connect ▸ service ▸ credential, then host actions. */
export function hostMenuEntries(host: Host, actions: HostMenuActions): MenuEntry[] {
  return [
    { kind: 'header', label: host.name },
    { kind: 'item', label: 'Connect', submenu: connectMenuEntries(host, actions) },
    { kind: 'separator' },
    ...(host.canManage ? [{ kind: 'item', label: 'Edit', onSelect: () => actions.edit(host) } as const] : []),
    { kind: 'item', label: 'Copy address', detail: primaryAddress(host), onSelect: () => actions.copyAddress(host) },
  ]
}

/** One entry per service, each opening its credentials (also used by the Connect button). */
export function connectMenuEntries(host: Host, actions: Pick<HostMenuActions, 'connect' | 'connectWithPrompt' | 'connectWeb'>): MenuEntry[] {
  const services: MenuEntry[] = host.services.map((service, index): MenuEntry => {
    if (!CONNECTABLE.has(service.protocol)) {
      return { kind: 'item', label: serviceLabel(service), detail: 'Coming soon', disabled: true }
    }

    if (WEB_PROTOCOLS.has(service.protocol)) {
      return { kind: 'item', label: serviceLabel(service), detail: servicePort(service), onSelect: () => actions.connectWeb(host, index) }
    }

    const credentials: MenuEntry[] = host.credentials.map((credential): MenuEntry => {
      const account = formatAccount(credential)
      const problem = credentialProblem(credential, service.protocol)
      return {
        kind: 'item',
        label: credential.label ?? account ?? 'Credential',
        detail: problem ?? (credential.label ? (account ?? undefined) : credential.hasPrivateKey ? 'SSH key' : undefined),
        disabled: problem !== null,
        onSelect: () => actions.connect(host, index, credential),
      }
    })

    return {
      kind: 'item',
      label: serviceLabel(service),
      detail: servicePort(service),
      submenu: [
        ...credentials,
        ...(credentials.length > 0 ? [{ kind: 'separator' } as const] : []),
        { kind: 'item', label: 'Enter credentials…', onSelect: () => actions.connectWithPrompt(host, index) },
      ],
    }
  })

  return services.length > 0 ? services : [{ kind: 'item', label: 'No services', detail: 'Edit the host to add one', disabled: true }]
}

/** The first connectable service with the first usable credential, for double-click connect. */
export function defaultConnection(host: Host): { serviceIndex: number; credential: HostCredential | null } | null {
  const serviceIndex = host.services.findIndex((s) => CONNECTABLE.has(s.protocol))
  if (serviceIndex === -1) {
    return null
  }
  const protocol = host.services[serviceIndex].protocol
  return { serviceIndex, credential: host.credentials.find((c) => credentialProblem(c, protocol) === null) ?? null }
}
