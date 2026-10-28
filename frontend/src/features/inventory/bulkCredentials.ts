import type { BulkCredentialMatch, BulkCredentialUpdate, Host, HostCredentialInput } from './types'

export type CredentialAction = 'add' | 'change' | 'remove'

export interface CredentialChange {
  key: number
  action: CredentialAction
  username: string
  domain: string
  password: string
  privateKey: string
  clearPassword: boolean
}

let nextKey = 1

export const newCredentialChange = (): CredentialChange => ({
  key: nextKey++,
  action: 'add',
  username: '',
  domain: '',
  password: '',
  privateKey: '',
  clearPassword: false,
})

const same = (a: string | null, b: string) => (a ?? '').trim().localeCompare(b.trim(), undefined, { sensitivity: 'accent' }) === 0

/** The account a change is about: its username, and its domain when given (otherwise any). */
const matches = (host: Host, change: CredentialChange, exactDomain: boolean) =>
  host.credentials.some((c) => same(c.username, change.username) && (exactDomain || change.domain.trim() ? same(c.domain, change.domain) : true))

/** How many hosts a credential change would change, in words. */
export function credentialImpact(change: CredentialChange, hosts: Host[]): string | null {
  if (!change.username.trim()) {
    return null
  }
  const hostWord = (n: number) => (n === 1 ? '1 host' : `${n} hosts`)
  if (change.action === 'add') {
    const count = hosts.filter((h) => !matches(h, change, true)).length
    return count === 0 ? 'All selected hosts already have it' : `Adds it to ${hostWord(count)}`
  }
  const count = hosts.filter((h) => matches(h, change, false)).length
  return count === 0 ? 'No selected host has it' : count === 1 ? '1 host has it' : `${count} hosts have it`
}

/** Whether a change has what it needs to be sent: an account, and for a change, something to change. */
export function isReady(change: CredentialChange): boolean {
  if (!change.username.trim()) {
    return false
  }
  return change.action !== 'change' || change.password !== '' || change.privateKey.trim() !== '' || change.clearPassword
}

/** The changes as the bulk update's addCredentials, updateCredentials and removeCredentials. */
export function toCredentialInputs(changes: CredentialChange[]) {
  const ready = changes.filter(isReady)
  const account = (c: CredentialChange): BulkCredentialMatch => ({ username: c.username.trim(), domain: c.domain.trim() || null })
  const secrets = (c: CredentialChange) => ({
    ...(c.password ? { password: c.password } : {}),
    ...(c.privateKey.trim() ? { privateKey: c.privateKey } : {}),
  })
  return {
    addCredentials: ready
      .filter((c) => c.action === 'add')
      .map((c): HostCredentialInput => ({ label: null, username: c.username.trim(), domain: c.domain.trim() || null, notes: null, ...secrets(c) })),
    updateCredentials: ready
      .filter((c) => c.action === 'change')
      .map((c): BulkCredentialUpdate => ({ ...account(c), ...secrets(c), ...(c.clearPassword ? { clearPassword: true } : {}) })),
    removeCredentials: ready.filter((c) => c.action === 'remove').map(account),
  }
}
