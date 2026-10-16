import type { HostAccessGrant } from './types'

/** Editor row: a grant plus a key so React can track rows while editing. */
export interface GrantDraft extends HostAccessGrant {
  key: number
}

let nextKey = 1
export const toGrantDraft = (grant: HostAccessGrant): GrantDraft => ({ ...grant, key: nextKey++ })
