import type { Host } from '../inventory/types'

/** A /24 subnet with at least one host, e.g. { prefix: '192.168.1', used: 12 }. */
export interface SubnetColumn {
  /** The first three octets, e.g. "192.168.1". */
  prefix: string
  /** How many of its addresses have a host. */
  used: number
}

/** Hosts by IPv4 address: one column per /24 subnet in use, one row per last octet (0–255). */
export interface IpGrid {
  subnets: SubnetColumn[]
  /** The hosts at an address; more than one means a conflict. */
  at: (prefix: string, octet: number) => Host[]
  /** Last octets used in at least one subnet. */
  usedOctets: Set<number>
}

export const OCTETS = Array.from({ length: 256 }, (_, i) => i)

const IPV4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/

/** Hosts' IPv4 addresses grouped into /24 subnets, ordered numerically (10.x before 192.x). IPv6 and hostname-only hosts are left out. */
export function buildIpGrid(hosts: Host[]): IpGrid {
  const byAddress = new Map<string, Host[]>()
  const subnets = new Map<string, { octets: number[]; used: Set<number> }>()
  const usedOctets = new Set<number>()

  for (const host of hosts) {
    const match = host.ipAddress ? IPV4.exec(host.ipAddress) : null
    if (!match) {
      continue
    }
    const octets = match.slice(1).map(Number)
    const prefix = octets.slice(0, 3).join('.')
    const last = octets[3]
    const key = `${prefix}.${last}`
    byAddress.set(key, [...(byAddress.get(key) ?? []), host])
    const subnet = subnets.get(prefix) ?? { octets: octets.slice(0, 3), used: new Set<number>() }
    subnet.used.add(last)
    subnets.set(prefix, subnet)
    usedOctets.add(last)
  }

  const ordered = [...subnets.entries()].sort(([, a], [, b]) => a.octets[0] - b.octets[0] || a.octets[1] - b.octets[1] || a.octets[2] - b.octets[2])
  return {
    subnets: ordered.map(([prefix, subnet]) => ({ prefix, used: subnet.used.size })),
    at: (prefix, octet) => byAddress.get(`${prefix}.${octet}`) ?? [],
    usedOctets,
  }
}

/** The grid as CSV, for spreadsheets: a header row of subnets, then one row per last octet. */
export function ipGridCsv(grid: IpGrid, octets: number[] = OCTETS): string {
  const quote = (value: string) => (/[",\r\n]/.test(value) ? `"${value.replaceAll('"', '""')}"` : value)
  const header = ['Address', ...grid.subnets.map((s) => `${s.prefix}.x`)]
  const rows = octets.map((octet) => [`.${octet}`, ...grid.subnets.map((s) => grid.at(s.prefix, octet).map((h) => h.name).join(' / '))])
  return [header, ...rows].map((row) => row.map(quote).join(',')).join('\r\n') + '\r\n'
}
