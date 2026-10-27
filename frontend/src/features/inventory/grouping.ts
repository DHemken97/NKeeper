import { OS_TAG } from './scanProposals'
import { formatAccount, PROTOCOL_INFO, primaryAddress, type Host } from './types'

/**
 * What hosts can be grouped by: a tag name, the operating system (the `os` tag scans record), the
 * services hosts offer, the usernames of their credentials, the host VMs run on, or whether they
 * are physical or virtual.
 */
export type GroupKey =
  | { kind: 'tag'; name: string }
  | { kind: 'os' }
  | { kind: 'service' }
  | { kind: 'username' }
  | { kind: 'hypervisor' }
  | { kind: 'machine' }

export type HostSort = 'name' | 'address' | 'updated'

/** The fields other than tags, with their URL form ("@service"), label and the label of hosts without one. */
const FIELDS = {
  os: { code: '@os', label: 'Operating system', missing: 'No operating system' },
  service: { code: '@service', label: 'Service', missing: 'No services' },
  username: { code: '@username', label: 'Username', missing: 'No username' },
  hypervisor: { code: '@runs-on', label: 'Runs on', missing: 'Not on a known host' },
  machine: { code: '@machine', label: 'Physical or VM', missing: '' },
} as const

type FieldKind = keyof typeof FIELDS

/** Every field other than tags, in the order pickers offer them. */
export const FIELD_KEYS: GroupKey[] = (Object.keys(FIELDS) as FieldKind[]).map((kind) => ({ kind }) as GroupKey)

/** URL form: a tag name, or a field's code such as "@service" or "@runs-on". */
export function encodeGroupKey(key: GroupKey): string {
  return key.kind === 'tag' ? key.name : FIELDS[key.kind].code
}

export function decodeGroupKey(value: string): GroupKey {
  const kind = (Object.keys(FIELDS) as FieldKind[]).find((k) => FIELDS[k].code === value)
  return kind ? ({ kind } as GroupKey) : { kind: 'tag', name: value }
}

export function groupKeyLabel(key: GroupKey): string {
  return key.kind === 'tag' ? key.name : FIELDS[key.kind].label
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

/** What grouping needs to know about the whole inventory, not just the hosts being grouped. */
interface Context {
  /** Host names by id, to label "Runs on" groups. */
  names: ReadonlyMap<string, string>
  /** Hosts that VMs run on. */
  hypervisors: ReadonlySet<string>
}

/**
 * The groups a host belongs to for a key, as a bucket key and a label; empty when it has none.
 * Tag values are matched case-insensitively. "Runs on" groups by the hypervisor's id (names can
 * repeat), labelled with its name; a hypervisor or physical host is in its own group (it runs on
 * its own hardware), and a nested hypervisor also in the group of the host it runs on. Only VMs
 * whose host isn't known have no group.
 */
function valuesFor(host: Host, key: GroupKey, { names, hypervisors }: Context): { key: string; label: string }[] {
  if (key.kind === 'service') {
    return [...new Set(host.services.map((s) => PROTOCOL_INFO[s.protocol].label))].map((label) => ({ key: label.toLowerCase(), label }))
  }
  if (key.kind === 'username') {
    const accounts = new Map<string, string>()
    for (const account of host.credentials.map(formatAccount)) {
      if (account && !accounts.has(account.toLowerCase())) {
        accounts.set(account.toLowerCase(), account)
      }
    }
    return [...accounts].map(([key, label]) => ({ key, label }))
  }
  if (key.kind === 'machine') {
    return [host.isVirtualMachine ? { key: 'vm', label: 'Virtual machine' } : { key: 'physical', label: 'Physical' }]
  }
  if (key.kind === 'hypervisor') {
    return [
      ...(hypervisors.has(host.id) || !host.isVirtualMachine ? [{ key: host.id, label: host.name }] : []),
      ...(host.hypervisorId ? [{ key: host.hypervisorId, label: names.get(host.hypervisorId) ?? 'A host you can’t see' }] : []),
    ]
  }
  const name = (key.kind === 'os' ? OS_TAG : key.name).toLowerCase()
  return host.tags
    .filter((t) => t.name.toLowerCase() === name)
    .map((t) => ({ key: (t.value ?? '').toLowerCase(), label: t.value || '(no value)' }))
}

function missingLabel(key: GroupKey): string {
  return key.kind === 'tag' ? `No ${key.name}` : FIELDS[key.kind].missing
}

/**
 * Groups (already sorted) hosts by each key in turn, e.g. [site, role] gives site → role → hosts.
 * A host with several values for a key (two `role` tags) appears under each; hosts without
 * the key are collected in a trailing "No <key>" group.
 * @param allHosts Every host, to name the hypervisors of "Runs on" groups (and know which hosts
 *   are hypervisors) even when a search hides them.
 */
export function buildTree(hosts: Host[], keys: GroupKey[], allHosts: Host[] = hosts, parentId = ''): TreeNode[] {
  const context: Context = {
    names: new Map(allHosts.map((h) => [h.id, h.name])),
    hypervisors: new Set(allHosts.flatMap((h) => (h.hypervisorId ? [h.hypervisorId] : []))),
  }
  return build(hosts, keys, context, parentId)
}

function build(hosts: Host[], keys: GroupKey[], context: Context, parentId: string): TreeNode[] {
  if (keys.length === 0) {
    return hosts.map((host) => ({ type: 'host', id: `${parentId}#${host.id}`, host }))
  }

  const [key, ...rest] = keys
  const buckets = new Map<string, { label: string; hosts: Host[] }>()
  const missing: Host[] = []

  for (const host of hosts) {
    const values = valuesFor(host, key, context)
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

  // A hypervisor heads its own "Runs on" group.
  if (key.kind === 'hypervisor') {
    for (const [bucketKey, bucket] of buckets) {
      bucket.hosts.sort((a, b) => Number(b.id === bucketKey) - Number(a.id === bucketKey))
    }
  }

  const keyId = encodeGroupKey(key)
  const groups: TreeGroup[] = [...buckets.entries()]
    .sort(([, a], [, b]) => collator.compare(a.label, b.label))
    .map(([bucketKey, bucket]) => makeGroup(`${parentId}/${keyId}=${bucketKey}`, key, bucket.label, false, bucket.hosts, rest, context))

  if (missing.length > 0) {
    groups.push(makeGroup(`${parentId}/${keyId}!`, key, missingLabel(key), true, missing, rest, context))
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
  context: Context,
): TreeGroup {
  return { type: 'group', id, key, label, missing, hostCount: hosts.length, children: build(hosts, rest, context, id) }
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
