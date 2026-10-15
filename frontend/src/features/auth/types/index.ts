/** Mirrors the API's CurrentUserResponse. `id` is the stable user identifier. */
export interface User {
  id: string
  userName: string
  email: string | null
  roles: string[]
}

export interface LoginCredentials {
  userNameOrEmail: string
  password: string
  rememberMe: boolean
}

export type AuthStatus = 'checking' | 'authenticated' | 'unauthenticated' | 'unavailable'
