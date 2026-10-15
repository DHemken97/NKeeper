import { api, ApiError, resetAntiforgeryToken } from '../../../lib/api'
import type { LoginCredentials, User } from '../types'

export const authApi = {
  async login(credentials: LoginCredentials): Promise<User> {
    // A 401 here means bad credentials, not an expired session.
    const user = await api.post<User>('/api/auth/login', { body: credentials, suppressUnauthorizedEvent: true })
    resetAntiforgeryToken()
    return user
  },

  async logout(): Promise<void> {
    await api.post<void>('/api/auth/logout')
    resetAntiforgeryToken()
  },

  /** Returns the signed-in user, or null when there is no valid session. */
  async getCurrentUser(signal?: AbortSignal): Promise<User | null> {
    try {
      return await api.get<User>('/api/auth/me', { signal, suppressUnauthorizedEvent: true })
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        return null
      }
      throw error
    }
  },
}
