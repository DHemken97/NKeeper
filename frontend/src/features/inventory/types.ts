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

export interface GroupReference {
  id: string
  name: string
}

export interface CredentialReference {
  id: string
  name: string
  username: string | null
  domain: string | null
}

export interface HostService {
  protocol: ServiceProtocol
  port: number | null
  label: string | null
}

export interface HostSummary {
  id: string
  name: string
  hostname: string | null
  ipAddress: string | null
  macAddress: string | null
  group: GroupReference | null
  tags: string[]
  services: HostService[]
  credentialCount: number
  updatedAtUtc: string
}

export interface Host {
  id: string
  name: string
  hostname: string | null
  ipAddress: string | null
  macAddress: string | null
  description: string | null
  group: GroupReference | null
  tags: string[]
  services: HostService[]
  credentials: CredentialReference[]
  createdAtUtc: string
  updatedAtUtc: string
  version: number
}

export interface HostInput {
  name: string
  hostname: string | null
  ipAddress: string | null
  macAddress: string | null
  description: string | null
  groupId: string | null
  tags: string[]
  services: HostService[]
  credentialIds: string[]
  version?: number
}

export type HostSortField = 'name' | 'address' | 'group' | 'updated'
export type SortDirection = 'asc' | 'desc'

export interface HostListQuery {
  search?: string
  groupId?: string
  ungrouped?: boolean
  tag?: string
  protocol?: ServiceProtocol
  sort?: HostSortField
  direction?: SortDirection
  page?: number
  pageSize?: number
}

export interface Paged<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
}

export interface Credential {
  id: string
  name: string
  username: string | null
  domain: string | null
  notes: string | null
  hasPassword: boolean
  hasPrivateKey: boolean
  hostCount: number
  createdAtUtc: string
  updatedAtUtc: string
  version: number
}

export interface CredentialInput {
  name: string
  username: string | null
  domain: string | null
  /** Omit (undefined) to keep the stored secret. */
  password?: string
  clearPassword?: boolean
  privateKey?: string
  clearPrivateKey?: boolean
  notes: string | null
  version?: number
}

export interface Group {
  id: string
  name: string
  description: string | null
  hostCount: number
}

export interface GroupInput {
  name: string
  description: string | null
}

export interface TagSummary {
  name: string
  hostCount: number
}

/** "DOMAIN\user", "user" or null. */
export function formatAccount(credential: Pick<CredentialReference, 'username' | 'domain'>): string | null {
  if (!credential.username) {
    return null
  }
  return credential.domain ? `${credential.domain}\\${credential.username}` : credential.username
}
