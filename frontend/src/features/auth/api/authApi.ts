import { api, apiUrl, ApiError, resetAntiforgeryToken } from '../../../lib/api'
import type { LoginCredentials, SignInOptions, User } from '../types'

export const authApi = {
  /** Never fails: without an answer, the sign-in page just offers passwords. */
  async getSignInOptions(signal?: AbortSignal): Promise<SignInOptions> {
    try {
      return await api.get<SignInOptions>('/api/auth/options', { signal, suppressUnauthorizedEvent: true })
    } catch {
      return { singleSignOnName: null }
    }
  },

  /** Where the browser goes to sign in with the identity provider; it comes back to `returnUrl`. */
  singleSignOnUrl(returnUrl: string, rememberMe: boolean): string {
    const query = new URLSearchParams({ returnUrl, rememberMe: String(rememberMe) })
    return apiUrl(`/api/auth/sso/login?${query}`)
  },

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
