import { PROTOCOL_INFO, primaryAddress, type Host } from './types'

/** What the tree can be grouped by: a tag name, or the services hosts offer. */
export type GroupKey = { kind: 'tag'; name: string } | { kind: 'service' }

export type HostSort = 'name' | 'address' | 'updated'

const SERVICE_KEY = '@service'

/** URL form: a tag name, or "@service". */
export function encodeGroupKey(key: GroupKey): string {
  return key.kind === 'service' ? SERVICE_KEY : key.name
}

export function decodeGroupKey(value: string): GroupKey {
  return value === SERVICE_KEY ? { kind: 'service' } : { kind: 'tag', name: value }
}

export function groupKeyLabel(key: GroupKey): string {
  return key.kind === 'service' ? 'Service' : key.name
}

export interface TreeGroup {
  type: 'group'
  /** Stable path id used for expand/collapse state, e.g. "site=DC-A/role=sql". */
  id: string
  key: GroupKey
  /** Display label; for missing values this reads e.g. "No site". */
  label: string
  /** True for the bucket of hosts that have no value for this key. */
  missing: boolean
  /** Distinct hosts anywhere under this group. */
  hostCount: number
  children: TreeNode[]
}

export interface TreeHost {
  type: 'host'
  /** Unique within the tree (a host can appear under several groups). */
  id: string
  host: Host
}

export type TreeNode = TreeGroup | TreeHost

const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true })

export function sortHosts(hosts: Host[], sort: HostSort): Host[] {
  const byName = (a: Host, b: Host) => collator.compare(a.name, b.name)
  const compare =
    sort === 'address'
      ? (a: Host, b: Host) => collator.compare(primaryAddress(a), primaryAddress(b)) || byName(a, b)
      : sort === 'updated'
        ? (a: Host, b: Host) => b.updatedAtUtc.localeCompare(a.updatedAtUtc) || byName(a, b)
        : byName
  return [...hosts].sort(compare)
}

/** The values a host has for a key; empty when it has none. Tag values are matched case-insensitively. */
function valuesFor(host: Host, key: GroupKey): string[] {
  if (key.kind === 'service') {
    return [...new Set(host.services.map((s) => PROTOCOL_INFO[s.protocol].label))]
  }
  const name = key.name.toLowerCase()
  return host.tags.filter((t) => t.name.toLowerCase() === name).map((t) => t.value ?? '')
}

function missingLabel(key: GroupKey): string {
  return key.kind === 'service' ? 'No services' : `No ${key.name}`
}

/**
 * Groups (already sorted) hosts by each key in turn, e.g. [site, role] gives site → role → hosts.
 * A host with several values for a key (two `role` tags) appears under each; hosts without
 * the key are collected in a trailing "No <key>" group.
 */
export function buildTree(hosts: Host[], keys: GroupKey[], parentId = ''): TreeNode[] {
  if (keys.length === 0) {
    return hosts.map((host) => ({ type: 'host', id: `${parentId}#${host.id}`, host }))
  }

  const [key, ...rest] = keys
  const buckets = new Map<string, { label: string; hosts: Host[] }>()
  const missing: Host[] = []

  for (const host of hosts) {
    const values = valuesFor(host, key)
    if (values.length === 0) {
      missing.push(host)
    }
    for (const value of values) {
      const bucketKey = value.toLowerCase()
      const bucket = buckets.get(bucketKey) ?? { label: value || '(no value)', hosts: [] }
      if (!bucket.hosts.includes(host)) {
        bucket.hosts.push(host)
      }
      buckets.set(bucketKey, bucket)
    }
  }

  const keyId = encodeGroupKey(key)
  const groups: TreeGroup[] = [...buckets.entries()]
    .sort(([, a], [, b]) => collator.compare(a.label, b.label))
    .map(([bucketKey, bucket]) => makeGroup(`${parentId}/${keyId}=${bucketKey}`, key, bucket.label, false, bucket.hosts, rest))

  if (missing.length > 0) {
    groups.push(makeGroup(`${parentId}/${keyId}!`, key, missingLabel(key), true, missing, rest))
  }
  return groups
}

function makeGroup(id: string, key: GroupKey, label: string, missing: boolean, hosts: Host[], rest: GroupKey[]): TreeGroup {
  return { type: 'group', id, key, label, missing, hostCount: hosts.length, children: buildTree(hosts, rest, id) }
}

/** Tag names in use across the inventory, for the group-by picker and tag autocomplete. */
export function tagNames(hosts: Host[]): string[] {
  const names = new Map<string, string>()
  for (const tag of hosts.flatMap((h) => h.tags)) {
    if (!names.has(tag.name.toLowerCase())) {
      names.set(tag.name.toLowerCase(), tag.name)
    }
  }
  return [...names.values()].sort(collator.compare)
}

/** Values in use for a tag name, for autocomplete. */
export function tagValues(hosts: Host[], name: string): string[] {
  const lower = name.trim().toLowerCase()
  const values = new Set(
    hosts.flatMap((h) => h.tags.filter((t) => t.name.toLowerCase() === lower && t.value).map((t) => t.value as string)),
  )
  return [...values].sort(collator.compare)
}
