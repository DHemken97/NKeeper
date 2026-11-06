import { useEffect, useSyncExternalStore } from 'react'
import { api } from '../../lib/api'

/** Mirrors the API's InventoryLimitsResponse: how much a host may hold, and how scans pre-select what they find. */
export interface InventoryLimits {
  maxServicesPerHost: number
  maxCredentialsPerHost: number
  maxTagsPerHost: number
  /** A scan that finds this many new users or fewer ticks them; more, and they all start unticked. */
  scanUsersPreselectedUpTo: number
  /** A folder is only zipped for download if the server would still have this much free beyond the folder's size. */
  zipReserveGigabytes: number
  updatedAtUtc: string | null
  ceilingServices: number
  ceilingCredentials: number
  ceilingTags: number
  ceilingScanUsers: number
  ceilingZipReserveGigabytes: number
}

/** The server's defaults, used until its answer comes (and if it can't be asked). */
export const DEFAULT_LIMITS: InventoryLimits = {
  maxServicesPerHost: 100,
  maxCredentialsPerHost: 100,
  maxTagsPerHost: 50,
  scanUsersPreselectedUpTo: 10,
  zipReserveGigabytes: 5,
  updatedAtUtc: null,
  ceilingServices: 1000,
  ceilingCredentials: 1000,
  ceilingTags: 500,
  ceilingScanUsers: 1000,
  ceilingZipReserveGigabytes: 1000,
}

export const limitsApi = {
  get: (signal?: AbortSignal) => api.get<InventoryLimits>('/api/settings/limits', { signal }),
  save: (input: Pick<InventoryLimits, 'maxServicesPerHost' | 'maxCredentialsPerHost' | 'maxTagsPerHost' | 'scanUsersPreselectedUpTo' | 'zipReserveGigabytes'>) =>
    api.put<InventoryLimits>('/api/admin/limits', { body: input }),
}

// One copy for the whole page: read once, replaced when an administrator saves new limits.
let current: InventoryLimits | null = null
let loading: Promise<void> | null = null
const listeners = new Set<() => void>()

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => void listeners.delete(listener)
}

/** Sets the limits everyone on this page uses (after saving them, or from a fresh read). */
export function setInventoryLimits(limits: InventoryLimits) {
  current = limits
  listeners.forEach((listener) => listener())
}

function load() {
  loading ??= limitsApi.get().then(
    (limits) => setInventoryLimits(limits),
    () => {
      // Keep the defaults; the server still enforces its own limits on save.
      loading = null
    },
  )
}

/** The limits in force: the defaults until the server answers. */
export function useInventoryLimits(): InventoryLimits {
  useEffect(() => {
    if (!current) {
      load()
    }
  }, [])
  return useSyncExternalStore(subscribe, () => current ?? DEFAULT_LIMITS)
}
