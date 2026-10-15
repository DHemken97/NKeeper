import { api } from '../../../lib/api'
import type { Host, HostInput } from '../types'

export const hostsApi = {
  /** The whole inventory; searching, grouping and sorting happen client-side. */
  list: (signal?: AbortSignal) => api.get<Host[]>('/api/hosts', { signal }),
  create: (input: HostInput) => api.post<Host>('/api/hosts', { body: input }),
  update: (id: string, input: HostInput) => api.put<Host>(`/api/hosts/${id}`, { body: input }),
  remove: (id: string) => api.delete(`/api/hosts/${id}`),
  /** Forget trusted SSH host keys; the next connection asks to trust the key again. */
  forgetSshHostKeys: (id: string) => api.delete(`/api/hosts/${id}/ssh-host-keys`),
}
