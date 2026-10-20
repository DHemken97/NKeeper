import { createContext, useContext } from 'react'
import type { AuthStatus, LoginCredentials, TwoFactorChallenge, User } from './types'

export interface AuthContextValue {
  status: AuthStatus
  user: User | null
  /** Resolves with the emailed-code step when the account needs it, else null (signed in). */
  login: (credentials: LoginCredentials) => Promise<TwoFactorChallenge | null>
  /** Finishes a sign-in with the emailed code. */
  verifyTwoFactor: (code: string) => Promise<void>
  logout: () => Promise<void>
  /** Re-checks the session, e.g. after the server was unreachable. */
  refresh: () => Promise<void>
  /** Records that the user chose a new password (the server already knows). */
  passwordChanged: () => void
}

export const AuthContext = createContext<AuthContextValue | null>(null)

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used inside <AuthProvider>.')
  }
  return context
}
