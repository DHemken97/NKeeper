import { api } from '../../../lib/api'
import type { HostAccessGrant, NewUserInput, UserDetails, UserSummary, UserUpdateInput } from '../types'

export const usersApi = {
  list: (signal?: AbortSignal) => api.get<UserSummary[]>('/api/users', { signal }),
  get: (id: string, signal?: AbortSignal) => api.get<UserDetails>(`/api/users/${id}`, { signal }),
  create: (input: NewUserInput) => api.post<UserDetails>('/api/users', { body: input }),
  update: (id: string, input: UserUpdateInput) => api.put<UserDetails>(`/api/users/${id}`, { body: input }),
  resetPassword: (id: string, password: string, mustChangePassword: boolean) =>
    api.post<void>(`/api/users/${id}/password`, { body: { password, mustChangePassword } }),
  setGrants: (id: string, grants: HostAccessGrant[]) =>
    api.put<UserDetails>(`/api/users/${id}/grants`, {
      body: { grants: grants.map(({ hostId, tagName, tagValue, level }) => ({ hostId, tagName, tagValue, level })) },
    }),
  remove: (id: string) => api.delete(`/api/users/${id}`),
}
