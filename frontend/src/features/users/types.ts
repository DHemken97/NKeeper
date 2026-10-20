import type { Host } from '../inventory/types'

export type UserRole = 'Administrator' | 'User'
export type AccessLevel = 'Use' | 'Manage'

export const ACCESS_LEVEL_INFO: Record<AccessLevel, { label: string; description: string }> = {
  Use: { label: 'Use', description: 'See and connect' },
  Manage: { label: 'Manage', description: 'Also edit and delete' },
}

export interface UserSummary {
  id: string
  userName: string
  email: string | null
  role: UserRole
  isDisabled: boolean
  mustChangePassword: boolean
  isLockedOut: boolean
  /** Signs in with Active Directory; its password is managed there. */
  isDirectoryAccount: boolean
  /** Signs in with the identity provider (single sign-on): no NKeeper password. */
  isSingleSignOnAccount?: boolean
  grantCount: number
  createdAtUtc: string
}

/** One host (`hostId`) or a group of hosts by tag (`tagName`, optionally `tagValue`). */
export interface HostAccessGrant {
  id?: string
  hostId: string | null
  hostName?: string | null
  tagName: string | null
  tagValue: string | null
  level: AccessLevel
}

export interface UserDetails extends Omit<UserSummary, 'grantCount'> {
  grants: HostAccessGrant[]
}

export interface NewUserInput {
  userName: string
  email: string
  role: UserRole
  password: string
  mustChangePassword: boolean
}

export interface UserUpdateInput {
  email: string
  role: UserRole
  isDisabled: boolean
}

/** Mirrors the server's rule (case-insensitive tag match; no value means any value). */
export function grantMatches(grant: Pick<HostAccessGrant, 'hostId' | 'tagName' | 'tagValue'>, host: Host): boolean {
  if (grant.hostId) {
    return grant.hostId === host.id
  }
  const name = grant.tagName?.trim().toLowerCase()
  const value = grant.tagValue?.trim().toLowerCase()
  return Boolean(name) && host.tags.some((t) => t.name.toLowerCase() === name && (!value || (t.value ?? '').toLowerCase() === value))
}

/** A random password that satisfies the default policy (12+ chars, upper, lower, digit). */
export function generatePassword(length = 16): string {
  const required = ['ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789']
  const all = required.join('') + '-_.!@#'
  const random = (max: number) => {
    // Rejection sampling avoids modulo bias.
    const limit = Math.floor(0x100000000 / max) * max
    const buffer = new Uint32Array(1)
    do {
      crypto.getRandomValues(buffer)
    } while (buffer[0] >= limit)
    return buffer[0] % max
  }
  const chars = [
    ...required.map((set) => set[random(set.length)]),
    ...Array.from({ length: length - required.length }, () => all[random(all.length)]),
  ]
  for (let i = chars.length - 1; i > 0; i--) {
    const j = random(i + 1)
    ;[chars[i], chars[j]] = [chars[j], chars[i]]
  }
  return chars.join('')
}
