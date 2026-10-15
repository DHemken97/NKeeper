import { api, toQueryString } from '../../../lib/api'
import type {
  Credential,
  CredentialInput,
  Group,
  GroupInput,
  Host,
  HostInput,
  HostListQuery,
  HostSummary,
  Paged,
  TagSummary,
} from '../types'

export const hostsApi = {
  list: (query: HostListQuery, signal?: AbortSignal) =>
    api.get<Paged<HostSummary>>(`/api/hosts${toQueryString({ ...query })}`, { signal }),
  get: (id: string, signal?: AbortSignal) => api.get<Host>(`/api/hosts/${id}`, { signal }),
  create: (input: HostInput) => api.post<Host>('/api/hosts', { body: input }),
  update: (id: string, input: HostInput) => api.put<Host>(`/api/hosts/${id}`, { body: input }),
  remove: (id: string) => api.delete(`/api/hosts/${id}`),
}

export const credentialsApi = {
  list: (signal?: AbortSignal) => api.get<Credential[]>('/api/credentials', { signal }),
  create: (input: CredentialInput) => api.post<Credential>('/api/credentials', { body: input }),
  update: (id: string, input: CredentialInput) => api.put<Credential>(`/api/credentials/${id}`, { body: input }),
  remove: (id: string) => api.delete(`/api/credentials/${id}`),
}

export const groupsApi = {
  list: (signal?: AbortSignal) => api.get<Group[]>('/api/groups', { signal }),
  create: (input: GroupInput) => api.post<Group>('/api/groups', { body: input }),
  update: (id: string, input: GroupInput) => api.put<Group>(`/api/groups/${id}`, { body: input }),
  remove: (id: string) => api.delete(`/api/groups/${id}`),
}

export const tagsApi = {
  list: (signal?: AbortSignal) => api.get<TagSummary[]>('/api/tags', { signal }),
}
