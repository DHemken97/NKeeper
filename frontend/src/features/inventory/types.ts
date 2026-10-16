/** Mirrors the API's ServiceProtocol enum (serialized as these exact strings). */
export const SERVICE_PROTOCOLS = [
  'Ssh',
  'Rdp',
  'Vnc',
  'Ssms',
  'Telnet',
  'Sftp',
  'Ftp',
  'Http',
  'Https',
  'WinRm',
  'PowerShell',
  'Rpc',
  'Kvm',
  'Idrac',
  'Other',
] as const

export type ServiceProtocol = (typeof SERVICE_PROTOCOLS)[number]

export const PROTOCOL_INFO: Record<ServiceProtocol, { label: string; name: string; defaultPort?: number }> = {
  Ssh: { label: 'SSH', name: 'SSH', defaultPort: 22 },
  Rdp: { label: 'RDP', name: 'Remote Desktop (RDP)', defaultPort: 3389 },
  Vnc: { label: 'VNC', name: 'VNC', defaultPort: 5900 },
  Ssms: { label: 'SSMS', name: 'SQL Server (SSMS)', defaultPort: 1433 },
  Telnet: { label: 'Telnet', name: 'Telnet', defaultPort: 23 },
  Sftp: { label: 'SFTP', name: 'SFTP', defaultPort: 22 },
  Ftp: { label: 'FTP', name: 'FTP', defaultPort: 21 },
  Http: { label: 'HTTP', name: 'Web (HTTP)', defaultPort: 80 },
  Https: { label: 'HTTPS', name: 'Web (HTTPS)', defaultPort: 443 },
  WinRm: { label: 'WinRM', name: 'WinRM', defaultPort: 5985 },
  PowerShell: { label: 'PowerShell', name: 'PowerShell Remoting', defaultPort: 5985 },
  Rpc: { label: 'RPC', name: 'Windows RPC', defaultPort: 135 },
  Kvm: { label: 'KVM', name: 'KVM / libvirt (over SSH)', defaultPort: 22 },
  Idrac: { label: 'iDRAC', name: 'Dell iDRAC (Redfish, console, RACADM)', defaultPort: 443 },
  Other: { label: 'Other', name: 'Other' },
}

/** A Zabbix-style tag, e.g. { name: 'env', value: 'production' }. */
export interface HostTag {
  name: string
  value: string | null
}

/** How an RDP connection is secured. Legacy RDP security (no TLS) is never offered. */
export type RdpSecurity = 'Auto' | 'Nla' | 'Tls'

export const RDP_SECURITY_INFO: Record<RdpSecurity, { label: string; description: string }> = {
  Auto: { label: 'Automatic', description: 'NLA when the server offers it, otherwise TLS' },
  Nla: { label: 'NLA only', description: 'Network Level Authentication, the Windows default' },
  Tls: { label: 'TLS only', description: 'For servers without NLA, such as xrdp' },
}

export interface HostService {
  protocol: ServiceProtocol
  port: number | null
  label: string | null
  /** RDP services only. */
  rdpSecurity?: RdpSecurity | null
  /** iDRAC services only: the iDRAC's own IP address (or hostname), where it is reached instead of the host's. */
  address?: string | null
}

/** A credential as returned by the API. Secrets are never included, only whether they are set. */
export interface HostCredential {
  id: string
  label: string | null
  username: string | null
  domain: string | null
  notes: string | null
  hasPassword: boolean
  hasPrivateKey: boolean
}

/** An SSH host key or RDP certificate the users trusted for one of the host's ports. */
export interface TrustedKey {
  protocol: ServiceProtocol
  port: number
  /** SSH: the key algorithm, e.g. "ssh-ed25519". RDP: "x509". */
  algorithm: string
  /** SHA-256. SSH: unpadded base64 (as OpenSSH prints it). RDP: colon-separated hex. */
  fingerprint: string
  trustedAtUtc: string
}

export interface Host {
  id: string
  name: string
  hostname: string | null
  ipAddress: string | null
  macAddress: string | null
  description: string | null
  /** A virtual machine (guest) rather than physical hardware. */
  isVirtualMachine: boolean
  /** For a VM: the host it runs on (a KVM or Proxmox node), when known. */
  hypervisorId: string | null
  /** The VM's identity on its hypervisor, recorded by scans, e.g. "libvirt:<uuid>" or "proxmox:<cluster>/<vmid>". */
  virtualMachineId: string | null
  /** When a scan last found the VM on that host; null when it was linked by hand. */
  hypervisorSeenAtUtc: string | null
  tags: HostTag[]
  services: HostService[]
  credentials: HostCredential[]
  trustedKeys: TrustedKey[]
  createdAtUtc: string
  updatedAtUtc: string
  version: number
  /** Whether the signed-in user may edit and delete this host. */
  canManage: boolean
}

/**
 * A credential in a save request. Send `id` to update an existing one (credentials left out are
 * deleted). Omit `password`/`privateKey` to keep the stored secret; set `clear…` to remove it.
 */
export interface HostCredentialInput {
  id?: string
  label: string | null
  username: string | null
  domain: string | null
  notes: string | null
  password?: string
  clearPassword?: boolean
  privateKey?: string
  clearPrivateKey?: boolean
  /** New credential starting from another credential's stored secrets (cloning a host). */
  copyFromCredentialId?: string
}

export interface HostInput {
  name: string
  hostname: string | null
  ipAddress: string | null
  macAddress: string | null
  description: string | null
  isVirtualMachine: boolean
  hypervisorId: string | null
  tags: HostTag[]
  services: HostService[]
  credentials: HostCredentialInput[]
  version?: number
}

/** "DOMAIN\user", "user" or null. */
export function formatAccount(credential: Pick<HostCredential, 'username' | 'domain'>): string | null {
  if (!credential.username) {
    return null
  }
  return credential.domain ? `${credential.domain}\\${credential.username}` : credential.username
}

/** "env: production", or just "backup" for a tag without a value. */
export function formatTag(tag: HostTag): string {
  return tag.value ? `${tag.name}: ${tag.value}` : tag.name
}

/** The address NKeeper connects to (and shows, copies and opens): the IP address when set, else the hostname. */
export function primaryAddress(host: Pick<Host, 'hostname' | 'ipAddress'>): string {
  return host.ipAddress ?? host.hostname ?? ''
}

/** Importing an mRemoteNG connection file (confCons.xml or an mRemoteNG XML export). */
export interface HostImportInput {
  /** The file's text. */
  content: string
  /** Only when one was set in mRemoteNG. */
  masterPassword?: string
  /** Add connections that match no host (by name). */
  add: boolean
  /** Update hosts that match a connection with its details. */
  update: boolean
  /** Delete hosts that match no connection in the file. */
  delete: boolean
  /** Report what would change without saving. */
  dryRun: boolean
}

/** What an import did (or, for a dry run, would do), by host name. */
export interface HostImportResult {
  dryRun: boolean
  added: string[]
  updated: string[]
  unchanged: string[]
  deleted: string[]
  skipped: { name: string; reason: string }[]
  warnings: string[]
}

/** A listening (internal scan) or open (external scan) TCP port; `protocol` is set when it can be an NKeeper service. */
export interface ScannedPort {
  port: number
  name: string | null
  protocol: ServiceProtocol | null
}

/** A local account that can log in, found by an internal scan. */
export interface ScannedUser {
  username: string
  uid: number
  fullName: string | null
  shell: string
}

/** A VM or container found on a scanned hypervisor (or its Proxmox cluster), with what NKeeper matched it to. */
export interface ScannedGuest {
  platform: 'libvirt' | 'proxmox-qemu' | 'proxmox-lxc'
  virtualMachineId: string
  name: string
  /** libvirt's state ("running", "shut off"…); null for Proxmox. */
  state: string | null
  /** The Proxmox node it's on; null for libvirt (the scanned host). */
  node: string | null
  onScannedHost: boolean
  macAddresses: string[]
  /** IP addresses it was seen with (libvirt leases and ARP, Proxmox static configuration). */
  addresses: string[]
  guestHostId: string | null
  guestHostName: string | null
  matchedBy: 'id' | 'mac' | 'name' | null
  hypervisorHostId: string | null
  hypervisorHostName: string | null
  /** Linking the guest to that host would change something. */
  linkProposed: boolean
  note: string | null
  /** For a guest that isn't in NKeeper: the host that could be added for it (null without an address). */
  newHost: ProposedHost | null
}

/** A host a scan could add for a VM or container NKeeper doesn't have yet. */
export interface ProposedHost {
  name: string
  hostname: string | null
  ipAddress: string | null
  macAddress: string | null
  description: string
}

/** A host to add from a scan, linked to the host it runs on. */
export interface ScannedHostInput extends ProposedHost {
  hypervisorId: string
  virtualMachineId: string
}

/** What a scan found. Nothing is saved until the user applies findings to the host. */
export interface ScanFindings {
  hostname: string | null
  /** The address the host was reached on. */
  ipAddress: string | null
  macAddress: string | null
  operatingSystem: string | null
  ports: ScannedPort[]
  isKvmHost: boolean
  /** Internal scans only. */
  users: ScannedUser[]
  notes: string[]
  /** Internal scans: what the host itself runs under (e.g. "kvm", "vmware"), or null for hardware. */
  virtualization: string | null
  /** Internal scans of hypervisors: their VMs and containers. */
  guests: ScannedGuest[]
}

/** Where a VM runs, as a scan of its hypervisor found it. */
export interface HypervisorLink {
  guestId: string
  hypervisorId: string
  virtualMachineId?: string
}

export interface ScanResponse {
  status: 'ready' | 'trustRequired'
  findings: ScanFindings | null
  hostKey: { algorithm: string; fingerprint: string } | null
}

/** How an internal scan logs in: a saved credential, or a one-off username and password. */
export interface InternalScanLogin {
  serviceIndex: number
  credentialId?: string
  username?: string
  password?: string
  /** RPC (Windows) scans only. */
  domain?: string
}

/** The same changes to many hosts: tags are removed, then set (replacing every value of the name), then added. */
export interface BulkHostUpdateInput {
  hostIds: string[]
  addTags: HostTag[]
  setTags: HostTag[]
  /** A null value removes every value of the tag. */
  removeTags: HostTag[]
  /** Set or (with a null id) clear the host the VMs run on; omit to leave it. */
  hypervisor?: { hypervisorId: string | null }
}
