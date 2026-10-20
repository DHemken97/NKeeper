import { api, apiUrl, ApiError, resetAntiforgeryToken } from '../../../lib/api'
import type { LoginCredentials, SignInOptions, TwoFactorChallenge, User } from '../types'

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

  /** The signed-in user, or the emailed-code step when the account needs it. */
  async login(credentials: LoginCredentials): Promise<User | TwoFactorChallenge> {
    // A 401 here means bad credentials, not an expired session.
    const result = await api.post<User | TwoFactorChallenge>('/api/auth/login', { body: credentials, suppressUnauthorizedEvent: true })
    if (!('twoFactorRequired' in result)) {
      resetAntiforgeryToken()
    }
    return result
  },

  /** This browser's sign-in waiting for its emailed code, or null. */
  async getTwoFactorChallenge(signal?: AbortSignal): Promise<TwoFactorChallenge | null> {
    try {
      return await api.get<TwoFactorChallenge>('/api/auth/two-factor', { signal, suppressUnauthorizedEvent: true })
    } catch {
      return null
    }
  },

  async verifyTwoFactor(code: string): Promise<User> {
    const user = await api.post<User>('/api/auth/two-factor', { body: { code }, suppressUnauthorizedEvent: true })
    resetAntiforgeryToken()
    return user
  },

  resendTwoFactor: () => api.post<TwoFactorChallenge>('/api/auth/two-factor/resend', { suppressUnauthorizedEvent: true }),

  cancelTwoFactor: () => api.post<void>('/api/auth/two-factor/cancel', { suppressUnauthorizedEvent: true }),

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
