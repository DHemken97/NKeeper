import { api } from '../../lib/api'
import type { Host, HostTag } from '../inventory/types'

/** Mirrors the API's ZabbixSettingsResponse. The API token itself never comes back. */
export interface ZabbixSettings {
  enabled: boolean
  url: string
  hasApiToken: boolean
  /** The server's certificate an administrator trusted, when it isn't publicly valid. */
  trustedCertificateFingerprint: string | null
  updatedAtUtc: string | null
}

export interface ZabbixSettingsInput {
  enabled: boolean
  url: string
  /** A new API token; null keeps the saved one. */
  apiToken: string | null
  /** A certificate fingerprint the administrator chose to trust after a test. */
  trustFingerprint: string | null
}

export interface ZabbixCertificate {
  fingerprint: string
  subject: string
  issuer: string
  expires: string
}

export type ZabbixTestResult =
  | { status: 'ready'; version: string; hostCount: number; certificate: null }
  | { status: 'trustRequired'; version: null; hostCount: null; certificate: ZabbixCertificate }

export interface ZabbixStatus {
  enabled: boolean
  /** The Zabbix frontend's address, ending in "/". */
  frontendUrl: string | null
}

export interface ZabbixHostSummary {
  hostId: string
  /** The technical name. */
  host: string
  /** The visible name. */
  name: string
  monitored: boolean
  ipAddresses: string[]
  dnsNames: string[]
  macAddresses: string[]
  operatingSystem: string | null
  tags: HostTag[]
}

export type ZabbixAvailability = 'Available' | 'Unavailable' | 'Unknown' | 'Mixed'

export interface ZabbixInterface {
  type: string
  ipAddress: string | null
  dns: string | null
  port: string
  useIp: boolean
  main: boolean
  availability: ZabbixAvailability
  error: string | null
}

export interface ZabbixItem {
  key: string
  name: string
  value: string | null
  units: string | null
  checkedAtUtc: string | null
}

export interface ZabbixProblem {
  eventId: string
  name: string
  /** 0 not classified … 5 disaster. */
  severity: number
  startedAtUtc: string
  acknowledged: boolean
  suppressed: boolean
}

export interface ZabbixHostOverview {
  host: ZabbixHostSummary
  description: string | null
  inMaintenance: boolean
  availability: ZabbixAvailability
  interfaces: ZabbixInterface[]
  groups: string[]
  /** Inventory fields with a value, by Zabbix field name (os, serialno_a…). */
  inventory: Record<string, string>
  items: ZabbixItem[]
  problems: ZabbixProblem[]
}

export interface ComparedHost {
  id: string
  name: string
  hostname: string | null
  ipAddress: string | null
  macAddress: string | null
  canManage: boolean
}

export type MatchedBy = 'mac' | 'ip' | 'hostname' | 'name'

export interface ZabbixPair {
  host: ComparedHost
  zabbix: ZabbixHostSummary
  matchedBy: MatchedBy | null
}

export interface ZabbixComparison {
  linked: ZabbixPair[]
  probableMatches: ZabbixPair[]
  zabbixOnly: ZabbixHostSummary[]
  nKeeperOnly: ComparedHost[]
  /** NKeeper hosts linked to a Zabbix host that no longer exists. */
  brokenLinks: ComparedHost[]
}

export interface ZabbixImportResult {
  created: Host[]
  skipped: { zabbixHostId: string; name: string; reason: string }[]
}

export const SEVERITIES = ['Not classified', 'Information', 'Warning', 'Average', 'High', 'Disaster'] as const

export const zabbixApi = {
  status: (signal?: AbortSignal) => api.get<ZabbixStatus>('/api/zabbix/status', { signal }),
  getSettings: (signal?: AbortSignal) => api.get<ZabbixSettings>('/api/admin/zabbix', { signal }),
  saveSettings: (input: ZabbixSettingsInput) => api.put<ZabbixSettings>('/api/admin/zabbix', { body: input }),
  /** Tests these (possibly unsaved) settings; an empty token means the saved one. */
  testSettings: (input: ZabbixSettingsInput) => api.post<ZabbixTestResult>('/api/admin/zabbix/test', { body: input }),
  overview: (hostId: string, signal?: AbortSignal) => api.get<ZabbixHostOverview>(`/api/hosts/${hostId}/zabbix`, { signal }),
  search: (text: string, signal?: AbortSignal) =>
    api.get<ZabbixHostSummary[]>(`/api/zabbix/hosts?search=${encodeURIComponent(text)}`, { signal }),
  comparison: (signal?: AbortSignal) => api.get<ZabbixComparison>('/api/zabbix/comparison', { signal }),
  /** Link hosts to Zabbix hosts (null unlinks); returns the hosts as they now are. */
  link: (links: { hostId: string; zabbixHostId: string | null }[]) => api.post<Host[]>('/api/zabbix/links', { body: { links } }),
  /** Add NKeeper hosts for Zabbix hosts, from Zabbix's data. */
  importHosts: (zabbixHostIds: string[], groupsAsTags: boolean) =>
    api.post<ZabbixImportResult>('/api/zabbix/import', { body: { zabbixHostIds, groupsAsTags } }),
}

/** Links into the Zabbix frontend. */
export const zabbixLinks = {
  host: (frontendUrl: string, hostId: string) => `${frontendUrl}zabbix.php?action=host.view&filter_host=&filter_hostids%5B%5D=${hostId}&filter_set=1`,
  problems: (frontendUrl: string, hostId: string) => `${frontendUrl}zabbix.php?action=problem.view&hostids%5B%5D=${hostId}&filter_set=1`,
  latestData: (frontendUrl: string, hostId: string) => `${frontendUrl}zabbix.php?action=latest.view&hostids%5B%5D=${hostId}&filter_set=1`,
}
