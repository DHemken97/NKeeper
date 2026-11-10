import { api } from '../../../lib/api'
import type {
  BulkHostUpdateInput,
  Host,
  HostImportInput,
  HostImportResult,
  HostInput,
  HypervisorLink,
  InternalScanLogin,
  LocalNetwork,
  NetworkScan,
  ScannedHostInput,
  ScanResponse,
  ServiceCheck,
} from '../types'

export const hostsApi = {
  /** The whole inventory; searching, grouping and sorting happen client-side. */
  list: (signal?: AbortSignal) => api.get<Host[]>('/api/hosts', { signal }),
  create: (input: HostInput) => api.post<Host>('/api/hosts', { body: input }),
  update: (id: string, input: HostInput) => api.put<Host>(`/api/hosts/${id}`, { body: input }),
  remove: (id: string) => api.delete(`/api/hosts/${id}`),
  /** Forget trusted SSH host keys and RDP certificates; the next connection asks again. */
  forgetTrustedKeys: (id: string) => api.delete(`/api/hosts/${id}/trusted-keys`),
  /** Import an mRemoteNG connection file. Administrators only. */
  importMRemoteNg: (input: HostImportInput) => api.post<HostImportResult>('/api/hosts/import', { body: input }),
  /** Internal scan: one SSH login that reads the host's own state. Nothing is saved. */
  scanInternal: (id: string, login: InternalScanLogin, trustFingerprint?: string) =>
    api.post<ScanResponse>(`/api/hosts/${id}/scan/internal`, { body: { ...login, trustFingerprint } }),
  /** External scan: the server probes the host's address over the network. Nothing is saved. */
  scanExternal: (id: string) => api.post<ScanResponse>(`/api/hosts/${id}/scan/external`, { body: {} }),
  /** Record where VMs run (from a scan of their hypervisor); returns the updated guests. */
  linkVirtualMachines: (links: HypervisorLink[]) => api.post<Host[]>('/api/hosts/hypervisor-links', { body: { links } }),
  /** Apply the same tag and "runs on" changes to many hosts; returns them as they now are. */
  bulkUpdate: (input: BulkHostUpdateInput) => api.post<Host[]>('/api/hosts/bulk', { body: input }),
  /** Add hosts for VMs a scan found, linked to their hypervisor. Administrators only. */
  createFromScan: (hosts: ScannedHostInput[]) => api.post<Host[]>('/api/hosts/from-scan', { body: { hosts } }),
  /** Combine duplicates into this host; they're deleted. Returns the combined host. */
  merge: (id: string, sourceIds: string[]) => api.post<Host>(`/api/hosts/${id}/merge`, { body: { sourceIds } }),
  /** Send a Wake-on-LAN packet to the host's MAC address, from the server. */
  wake: (id: string) => api.post<{ sentTo: string[] }>(`/api/hosts/${id}/wake`),
}

export const healthApi = {
  /** The latest checks of every service the user can see. */
  list: (signal?: AbortSignal) => api.get<ServiceCheck[]>('/api/health', { signal }),
  /** Check one host's services now. */
  check: (hostId: string) => api.post<ServiceCheck[]>(`/api/health/hosts/${hostId}/check`),
}

/** Network (subnet) scans, run on the server. Administrators only. */
export const networkScanApi = {
  /** The networks the server is on, to suggest as targets. */
  networks: () => api.get<LocalNetwork[]>('/api/network-scan/networks'),
  /** The user's most recent scan, or null when there's none. */
  latest: async () => (await api.get<NetworkScan | undefined>('/api/network-scan/latest')) ?? null,
  /** Starts sweeping the subnet; poll `get` for progress. The user must have acknowledged the warning. */
  start: (subnet: string) => api.post<NetworkScan>('/api/network-scan', { body: { subnet, acknowledged: true } }),
  get: (id: string, signal?: AbortSignal) => api.get<NetworkScan>(`/api/network-scan/${id}`, { signal }),
  cancel: (id: string) => api.delete(`/api/network-scan/${id}`),
}
