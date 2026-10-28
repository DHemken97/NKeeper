import { buildTree, groupKeyLabel, type GroupKey, type TreeGroup } from '../inventory/grouping'
import { OS_TAG } from '../inventory/scanProposals'
import { tagSearchTerm } from '../inventory/search'
import type { Host } from '../inventory/types'

/** One combination of values, e.g. site = DC-A, service = SSH, and the hosts that have it. */
export interface InventoryRow {
  /** Stable id from the tree, e.g. "/site=dc-a/@service=ssh". */
  id: string
  /** One cell per field, in the order chosen. */
  values: { label: string; missing: boolean }[]
  hosts: Host[]
  /** A workspace search for the row's tag and OS values (other fields can't be searched for exactly). */
  search: string
}

export interface InventoryReport {
  keys: GroupKey[]
  rows: InventoryRow[]
  /** Distinct hosts in the report; rows can add up to more, as a host with two values is counted under each. */
  hostCount: number
}

/**
 * Host counts for each combination of values of the given fields, grouped as the workspace tree
 * groups them: values match case-insensitively, a host with several values (two `role` tags, SSH
 * and RDP) counts under each, and hosts without a value fall in a trailing "No …" row.
 */
export function buildInventory(hosts: Host[], keys: GroupKey[]): InventoryReport {
  const rows: InventoryRow[] = []
  const visit = (group: TreeGroup, path: InventoryRow['values'], terms: string[]) => {
    const values = [...path, { label: group.label, missing: group.missing }]
    const tagName = group.key.kind === 'tag' ? group.key.name : group.key.kind === 'os' ? OS_TAG : null
    const nextTerms = tagName && !group.missing && group.label !== '(no value)' ? [...terms, tagSearchTerm(tagName, group.label)] : terms
    const subgroups = group.children.filter((child): child is TreeGroup => child.type === 'group')
    if (subgroups.length === 0) {
      const leaves = group.children.flatMap((child) => (child.type === 'host' ? [child.host] : []))
      rows.push({ id: group.id, values, hosts: leaves, search: nextTerms.join(' ') })
    }
    subgroups.forEach((child) => visit(child, values, nextTerms))
  }
  if (keys.length > 0) {
    buildTree(hosts, keys).forEach((node) => node.type === 'group' && visit(node, [], []))
  }
  return { keys, rows, hostCount: keys.length > 0 ? hosts.length : 0 }
}

/** The report as CSV: one column per field, then the host count and the hosts' names. */
export function inventoryCsv(report: InventoryReport): string {
  const quote = (value: string) => (/[",\r\n]/.test(value) ? `"${value.replaceAll('"', '""')}"` : value)
  const rows: string[][] = [
    [...report.keys.map(groupKeyLabel), 'Hosts', 'Host names'],
    ...report.rows.map((row) => [
      ...row.values.map((v) => (v.missing ? '' : v.label)),
      String(row.hosts.length),
      row.hosts.map((h) => h.name).join('; '),
    ]),
  ]
  return rows.map((row) => row.map(quote).join(',')).join('\r\n') + '\r\n'
}
