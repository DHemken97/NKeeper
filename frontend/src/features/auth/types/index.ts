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
  /** Signs in with the identity provider (single sign-on): no NKeeper password. */
  isSingleSignOnAccount?: boolean
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

/** The password was right; the account also needs the code just emailed. */
export interface TwoFactorChallenge {
  twoFactorRequired: true
  /** Where the code went, partly hidden (j•••@example.com). */
  codeSentTo: string
}

/** Ways to sign in besides a password. */
export interface SignInOptions {
  /** The identity provider's name for the "Sign in with …" button, or null without single sign-on. */
  singleSignOnName: string | null
}

export type AuthStatus = 'checking' | 'authenticated' | 'unauthenticated' | 'unavailable'
