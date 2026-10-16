import { PROTOCOL_INFO, primaryAddress, type Host } from './types'

/** What the tree can be grouped by: a tag name, the services hosts offer, or the host VMs run on. */
export type GroupKey = { kind: 'tag'; name: string } | { kind: 'service' } | { kind: 'hypervisor' }

export type HostSort = 'name' | 'address' | 'updated'

const SERVICE_KEY = '@service'
const HYPERVISOR_KEY = '@runs-on'

/** URL form: a tag name, "@service" or "@runs-on". */
export function encodeGroupKey(key: GroupKey): string {
  return key.kind === 'service' ? SERVICE_KEY : key.kind === 'hypervisor' ? HYPERVISOR_KEY : key.name
}

export function decodeGroupKey(value: string): GroupKey {
  return value === SERVICE_KEY ? { kind: 'service' } : value === HYPERVISOR_KEY ? { kind: 'hypervisor' } : { kind: 'tag', name: value }
}

export function groupKeyLabel(key: GroupKey): string {
  return key.kind === 'service' ? 'Service' : key.kind === 'hypervisor' ? 'Runs on' : key.name
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

/**
 * The groups a host belongs to for a key, as a bucket key and a label; empty when it has none.
 * Tag values are matched case-insensitively. "Runs on" groups by the hypervisor's id (names can
 * repeat), labelled with its name.
 */
function valuesFor(host: Host, key: GroupKey, names: ReadonlyMap<string, string>): { key: string; label: string }[] {
  if (key.kind === 'service') {
    return [...new Set(host.services.map((s) => PROTOCOL_INFO[s.protocol].label))].map((label) => ({ key: label.toLowerCase(), label }))
  }
  if (key.kind === 'hypervisor') {
    return host.hypervisorId ? [{ key: host.hypervisorId, label: names.get(host.hypervisorId) ?? 'A host you can’t see' }] : []
  }
  const name = key.name.toLowerCase()
  return host.tags
    .filter((t) => t.name.toLowerCase() === name)
    .map((t) => ({ key: (t.value ?? '').toLowerCase(), label: t.value || '(no value)' }))
}

function missingLabel(key: GroupKey): string {
  return key.kind === 'service' ? 'No services' : key.kind === 'hypervisor' ? 'Not on a known host' : `No ${key.name}`
}

/**
 * Groups (already sorted) hosts by each key in turn, e.g. [site, role] gives site → role → hosts.
 * A host with several values for a key (two `role` tags) appears under each; hosts without
 * the key are collected in a trailing "No <key>" group.
 * @param allHosts Every host, to name the hypervisors of "Runs on" groups even when a search hides them.
 */
export function buildTree(hosts: Host[], keys: GroupKey[], allHosts: Host[] = hosts, parentId = ''): TreeNode[] {
  const names = new Map(allHosts.map((h) => [h.id, h.name]))
  return build(hosts, keys, names, parentId)
}

function build(hosts: Host[], keys: GroupKey[], names: ReadonlyMap<string, string>, parentId: string): TreeNode[] {
  if (keys.length === 0) {
    return hosts.map((host) => ({ type: 'host', id: `${parentId}#${host.id}`, host }))
  }

  const [key, ...rest] = keys
  const buckets = new Map<string, { label: string; hosts: Host[] }>()
  const missing: Host[] = []

  for (const host of hosts) {
    const values = valuesFor(host, key, names)
    if (values.length === 0) {
      missing.push(host)
    }
    for (const value of values) {
      const bucketKey = value.key
      const bucket = buckets.get(bucketKey) ?? { label: value.label, hosts: [] }
      if (!bucket.hosts.includes(host)) {
        bucket.hosts.push(host)
      }
      buckets.set(bucketKey, bucket)
    }
  }

  const keyId = encodeGroupKey(key)
  const groups: TreeGroup[] = [...buckets.entries()]
    .sort(([, a], [, b]) => collator.compare(a.label, b.label))
    .map(([bucketKey, bucket]) => makeGroup(`${parentId}/${keyId}=${bucketKey}`, key, bucket.label, false, bucket.hosts, rest, names))

  if (missing.length > 0) {
    groups.push(makeGroup(`${parentId}/${keyId}!`, key, missingLabel(key), true, missing, rest, names))
  }
  return groups
}

function makeGroup(
  id: string,
  key: GroupKey,
  label: string,
  missing: boolean,
  hosts: Host[],
  rest: GroupKey[],
  names: ReadonlyMap<string, string>,
): TreeGroup {
  return { type: 'group', id, key, label, missing, hostCount: hosts.length, children: build(hosts, rest, names, id) }
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
