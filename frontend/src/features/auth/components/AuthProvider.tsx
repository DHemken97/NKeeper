import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { ApiError, onUnauthorized } from '../../../lib/api'
import { authApi } from '../api/authApi'
import { AuthContext, type AuthContextValue } from '../context'
import type { AuthStatus, LoginCredentials, User } from '../types'

/**
 * Owns the client-side view of the session. The server is the source of truth: on load the
 * session is verified with GET /api/auth/me, never assumed from earlier visits.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('checking')
  const [user, setUser] = useState<User | null>(null)

  const applyUser = useCallback((next: User | null) => {
    setUser(next)
    setStatus(next ? 'authenticated' : 'unauthenticated')
  }, [])

  const checkSession = useCallback(
    (signal?: AbortSignal) =>
      authApi.getCurrentUser(signal).then(applyUser, () => {
        if (signal?.aborted) {
          return
        }
        // Server unreachable or failing: don't pretend the user is signed out.
        setUser(null)
        setStatus('unavailable')
      }),
    [applyUser],
  )

  useEffect(() => {
    const controller = new AbortController()
    checkSession(controller.signal)
    return () => controller.abort()
  }, [checkSession])

  // Any 401 from a later API call means the session expired or was revoked elsewhere.
  useEffect(() => onUnauthorized(() => applyUser(null)), [applyUser])

  const login = useCallback(
    async (credentials: LoginCredentials) => {
      applyUser(await authApi.login(credentials))
    },
    [applyUser],
  )

  const logout = useCallback(async () => {
    try {
      await authApi.logout()
    } catch (error) {
      // An expired session is already logged out; anything else is reported to the caller.
      if (!(error instanceof ApiError && error.status === 401)) {
        throw error
      }
    }
    applyUser(null)
  }, [applyUser])

  const refresh = useCallback(async () => {
    setStatus('checking')
    await checkSession()
  }, [checkSession])

  const passwordChanged = useCallback(() => setUser((current) => (current ? { ...current, mustChangePassword: false } : current)), [])

  const value = useMemo<AuthContextValue>(
    () => ({ status, user, login, logout, refresh, passwordChanged }),
    [status, user, login, logout, refresh, passwordChanged],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
