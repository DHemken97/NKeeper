/** Mirrors the API's CurrentUserResponse. `id` is the stable user identifier. */
export interface User {
  id: string
  userName: string
  email: string | null
  roles: string[]
  /** An administrator set this password; the user must choose a new one before continuing. */
  mustChangePassword: boolean
  /** An Active Directory account: its password is changed in Windows, not in NKeeper. */
  isDirectoryAccount: boolean
  /** The domain login (CORP\jdoe) this sign-in can pass on to RDP sessions, or null. */
  signInAccount: string | null
  /** The signed-in user's saved host-tree grouping levels. */
  workspaceGroupBy?: string[]
  /** The signed-in user's saved host-tree sort. */
  workspaceSort?: 'name' | 'address' | 'updated'
}

export const ADMINISTRATOR_ROLE = 'Administrator'

export function isAdministrator(user: User | null | undefined): boolean {
  return user?.roles.includes(ADMINISTRATOR_ROLE) ?? false
}

export interface LoginCredentials {
  userNameOrEmail: string
  password: string
  rememberMe: boolean
}

export type AuthStatus = 'checking' | 'authenticated' | 'unauthenticated' | 'unavailable'
