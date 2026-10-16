import { api } from '../../../lib/api'
import type { Host, HostImportInput, HostImportResult, HostInput, InternalScanLogin, ScanResponse } from '../types'

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
}
