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
  tags: HostTag[]
  services: HostService[]
  credentials: HostCredential[]
  trustedKeys: TrustedKey[]
  createdAtUtc: string
  updatedAtUtc: string
  version: number
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
}

export interface HostInput {
  name: string
  hostname: string | null
  ipAddress: string | null
  macAddress: string | null
  description: string | null
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

/** The address to show for a host: hostname first, else IP. */
export function primaryAddress(host: Pick<Host, 'hostname' | 'ipAddress'>): string {
  return host.hostname ?? host.ipAddress ?? ''
}
