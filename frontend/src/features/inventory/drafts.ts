import { isSupportedProtocol, type Host, type HostCredential, type HostInput, type RdpSecurity, type ServiceProtocol } from './types'

/** Editor state for a host. Each list item has a `key` so React can track rows while editing. */

export interface ServiceDraft {
  key: number
  protocol: ServiceProtocol
  /** Kept as text so the field can be empty while typing. */
  port: string
  label: string
  /** Only used for RDP services. */
  rdpSecurity: RdpSecurity
  /** Only used for iDRAC services: the iDRAC's own address. */
  address: string
}

export interface TagDraft {
  key: number
  name: string
  value: string
}

export interface CredentialDraft {
  key: number
  /** Set for credentials that already exist on the server. */
  id?: string
  /** For a cloned host: the original credential whose stored secrets the server copies. */
  copyFrom?: string
  label: string
  username: string
  domain: string
  notes: string
  /** A new password to store; empty keeps the current one. */
  password: string
  clearPassword: boolean
  privateKey: string
  clearPrivateKey: boolean
  /** Whether the server already holds a secret (it is never sent to the browser). */
  hasPassword: boolean
  hasPrivateKey: boolean
}

export interface HostDraft {
  name: string
  hostname: string
  ipAddress: string
  macAddress: string
  description: string
  isVirtualMachine: boolean
  /** The host a VM runs on; empty for none. */
  hypervisorId: string
  /** Kept as text so the fields can be empty while typing. */
  cpuCores: string
  /** In GiB; decimals allowed. */
  memoryGiB: string
  serialNumber: string
  /** The jump host, or empty to connect directly. */
  jumpHostId: string
  /** The jump host's credential, or empty for its first usable one. */
  jumpCredentialId: string
  /** Plugin values, by plugin ID then field name; empty for none. */
  plugins: Record<string, Record<string, string>>
  /** The plugin values as saved, so only changed ones are sent (others belong to plugins this browser may not know). */
  savedPlugins: Record<string, Record<string, string>>
  tags: TagDraft[]
  services: ServiceDraft[]
  credentials: CredentialDraft[]
}

let nextKey = 1
const key = () => nextKey++

export function newServiceDraft(
  protocol: ServiceProtocol = 'Ssh',
  port: number | null = null,
  label: string | null = null,
  rdpSecurity: RdpSecurity | null = null,
  address: string | null = null,
): ServiceDraft {
  return {
    key: key(),
    protocol,
    port: port === null ? '' : String(port),
    label: label ?? '',
    rdpSecurity: rdpSecurity ?? 'Auto',
    address: address ?? '',
  }
}

export function newTagDraft(name = '', value: string | null = ''): TagDraft {
  return { key: key(), name, value: value ?? '' }
}

export function newCredentialDraft(existing?: HostCredential): CredentialDraft {
  return {
    key: key(),
    id: existing?.id,
    label: existing?.label ?? '',
    username: existing?.username ?? '',
    domain: existing?.domain ?? '',
    notes: existing?.notes ?? '',
    password: '',
    clearPassword: false,
    privateKey: '',
    clearPrivateKey: false,
    hasPassword: existing?.hasPassword ?? false,
    hasPrivateKey: existing?.hasPrivateKey ?? false,
  }
}

export function toHostDraft(host: Host | null): HostDraft {
  return {
    name: host?.name ?? '',
    hostname: host?.hostname ?? '',
    ipAddress: host?.ipAddress ?? '',
    macAddress: host?.macAddress ?? '',
    description: host?.description ?? '',
    isVirtualMachine: host?.isVirtualMachine ?? false,
    hypervisorId: host?.hypervisorId ?? '',
    cpuCores: host?.cpuCores ? String(host.cpuCores) : '',
    memoryGiB: host?.memoryMiB ? String(Math.round((host.memoryMiB / 1024) * 100) / 100) : '',
    serialNumber: host?.serialNumber ?? '',
    jumpHostId: host?.jumpHostId ?? '',
    jumpCredentialId: host?.jumpCredentialId ?? '',
    plugins: structuredClone(host?.plugins ?? {}),
    savedPlugins: host?.plugins ?? {},
    tags: host?.tags.map((t) => newTagDraft(t.name, t.value)) ?? [],
    // Services of types NKeeper can't connect with can't be saved; the editor says they'll go.
    services:
      host?.services.filter((s) => isSupportedProtocol(s.protocol)).map((s) => newServiceDraft(s.protocol, s.port, s.label, s.rdpSecurity, s.address)) ?? [],
    credentials: host?.credentials.map((c) => newCredentialDraft(c)) ?? [],
  }
}

/** A new host prefilled from an existing one: same details, a "(copy)" name, and its credentials'
 * secrets copied on the server (they never reach the browser). Trusted keys are not copied. */
export function toCloneDraft(source: Host): HostDraft {
  const draft = toHostDraft(source)
  return {
    ...draft,
    name: `${source.name} (copy)`,
    // A serial number belongs to one machine, as do the plugins' values (e.g. its Zabbix host).
    serialNumber: '',
    plugins: {},
    savedPlugins: {},
    credentials: source.credentials.map((c) => ({ ...newCredentialDraft(c), id: undefined, copyFrom: c.id })),
  }
}

const blankToNull = (value: string) => (value.trim() === '' ? null : value.trim())

/** GiB as typed (e.g. "64" or "1.5") to whole MiB; unreadable text is sent as-is for the server to reject. */
function memoryMiB(gib: string): number | null {
  const text = gib.trim().replace(',', '.')
  return text === '' ? null : Math.round(Number(text) * 1024)
}

/** The plugin values the draft changes (null removes one), or nothing when it changes none. */
function changedPlugins(draft: HostDraft): Pick<HostInput, 'plugins'> {
  const changes: Record<string, Record<string, string | null>> = {}
  for (const pluginId of new Set([...Object.keys(draft.plugins), ...Object.keys(draft.savedPlugins)])) {
    const now = draft.plugins[pluginId] ?? {}
    const saved = draft.savedPlugins[pluginId] ?? {}
    for (const field of new Set([...Object.keys(now), ...Object.keys(saved)])) {
      const value = blankToNull(now[field] ?? '')
      if (value !== (saved[field] ?? null)) {
        changes[pluginId] = { ...changes[pluginId], [field]: value }
      }
    }
  }
  return Object.keys(changes).length > 0 ? { plugins: changes } : {}
}

export function toHostInput(draft: HostDraft, version?: number): HostInput {
  return {
    name: draft.name.trim(),
    hostname: blankToNull(draft.hostname),
    ipAddress: blankToNull(draft.ipAddress),
    macAddress: blankToNull(draft.macAddress),
    description: blankToNull(draft.description),
    isVirtualMachine: draft.isVirtualMachine,
    hypervisorId: draft.isVirtualMachine && draft.hypervisorId ? draft.hypervisorId : null,
    cpuCores: draft.cpuCores.trim() === '' ? null : Number(draft.cpuCores),
    memoryMiB: memoryMiB(draft.memoryGiB),
    serialNumber: blankToNull(draft.serialNumber),
    jumpHostId: draft.jumpHostId || null,
    jumpCredentialId: draft.jumpHostId && draft.jumpCredentialId ? draft.jumpCredentialId : null,
    ...changedPlugins(draft),
    // Rows left completely empty are ignored rather than rejected.
    tags: draft.tags
      .filter((t) => t.name.trim() || t.value.trim())
      .map((t) => ({ name: t.name.trim(), value: blankToNull(t.value) })),
    services: draft.services.map((s) => ({
      protocol: s.protocol,
      port: s.port === '' ? null : Number(s.port),
      label: blankToNull(s.label),
      ...(s.protocol === 'Rdp' ? { rdpSecurity: s.rdpSecurity } : {}),
      ...(s.protocol === 'Idrac' ? { address: blankToNull(s.address) } : {}),
    })),
    credentials: draft.credentials.map((c) => ({
      ...(c.id ? { id: c.id } : {}),
      ...(c.copyFrom ? { copyFromCredentialId: c.copyFrom } : {}),
      label: blankToNull(c.label),
      username: blankToNull(c.username),
      domain: blankToNull(c.domain),
      notes: blankToNull(c.notes),
      // Secrets are sent exactly as typed, and only when set or explicitly cleared.
      ...(c.clearPassword ? { clearPassword: true } : c.password ? { password: c.password } : {}),
      ...(c.clearPrivateKey ? { clearPrivateKey: true } : c.privateKey.trim() ? { privateKey: c.privateKey } : {}),
    })),
    version,
  }
}
