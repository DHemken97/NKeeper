/** Mirrors the API's CurrentUserResponse. `id` is the stable user identifier. */
export interface User {
  id: string
  userName: string
  email: string | null
  roles: string[]
  /** An administrator set this password; the user must choose a new one before continuing. */
  mustChangePassword: boolean
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
