import { describe, expect, it } from 'vitest'
import { buildTree, sortHosts, tagNames, type TreeGroup, type TreeNode } from './grouping'
import { matchesSearch, parseSearch, tagSearchTerm } from './search'
import { primaryAddress, type Host, type HostTag, type ServiceProtocol } from './types'

function host(name: string, tags: HostTag[] = [], options: Partial<Host> & { protocols?: ServiceProtocol[] } = {}): Host {
  const { protocols = [], ...rest } = options
  return {
    id: name,
    name,
    hostname: `${name}.example.com`,
    ipAddress: null,
    macAddress: null,
    description: null,
    isVirtualMachine: false,
    hypervisorId: null,
    virtualMachineId: null,
    hypervisorSeenAtUtc: null,
    cpuCores: null,
    memoryMiB: null,
    serialNumber: null,
    jumpHostId: null,
    jumpCredentialId: null,
    zabbixHostId: null,
    tags,
    services: protocols.map((protocol) => ({ protocol, port: null, label: null })),
    credentials: [],
    trustedKeys: [],
    canManage: true,
    createdAtUtc: '2026-10-01T00:00:00Z',
    updatedAtUtc: '2026-10-01T00:00:00Z',
    version: 1,
    ...rest,
  }
}

const t = (name: string, value: string | null = null): HostTag => ({ name, value })

/** Compact outline of a tree, e.g. ["DC-A (2)", ["sql01", "web01"]]. */
function outline(nodes: TreeNode[]): unknown[] {
  return nodes.flatMap((node) =>
    node.type === 'host' ? [node.host.name] : [`${node.label} (${node.hostCount})`, outline((node as TreeGroup).children)],
  )
}

describe('buildTree', () => {
  const hosts = sortHosts(
    [
      host('web01', [t('site', 'DC-A'), t('role', 'web')], { protocols: ['Ssh', 'Https'] }),
      host('sql01', [t('site', 'dc-a'), t('role', 'sql'), t('role', 'web')], { protocols: ['Rdp'] }),
      host('fw01', [t('site', 'Branch')]),
      host('laptop'),
    ],
    'name',
  )

  it('lists hosts flat when not grouped', () => {
    expect(outline(buildTree(hosts, []))).toEqual(['fw01', 'laptop', 'sql01', 'web01'])
  })

  it('groups by a tag case-insensitively, with hosts missing the tag last', () => {
    // "DC-A" and "dc-a" are one group, labelled with the first spelling in sort order.
    expect(outline(buildTree(hosts, [{ kind: 'tag', name: 'Site' }]))).toEqual([
      'Branch (1)',
      ['fw01'],
      'dc-a (2)',
      ['sql01', 'web01'],
      'No Site (1)',
      ['laptop'],
    ])
  })

  it('nests levels and shows a host under every value it has', () => {
    const tree = buildTree(hosts, [{ kind: 'tag', name: 'site' }, { kind: 'tag', name: 'role' }])

    expect(outline(tree)[3]).toEqual(['sql (1)', ['sql01'], 'web (2)', ['sql01', 'web01']])
    const ids: string[] = []
    const collect = (nodes: TreeNode[]) =>
      nodes.forEach((node) => {
        ids.push(node.id)
        if (node.type === 'group') {
          collect(node.children)
        }
      })
    collect(tree)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('groups by service', () => {
    expect(outline(buildTree(hosts, [{ kind: 'service' }]))).toEqual([
      'HTTPS (1)',
      ['web01'],
      'RDP (1)',
      ['sql01'],
      'SSH (1)',
      ['web01'],
      'No services (2)',
      ['fw01', 'laptop'],
    ])
  })

  it('groups by the host VMs run on, with that host at the top of its group', () => {
    const vms = [
      host('app01', [t('site', 'DC-A')], { isVirtualMachine: true, hypervisorId: 'node1' }),
      host('app02', [t('site', 'DC-B')], { isVirtualMachine: true, hypervisorId: 'node1' }),
      host('db01', [], { isVirtualMachine: true, hypervisorId: 'node2' }),
      host('orphan', [], { isVirtualMachine: true, hypervisorId: 'gone' }),
      host('node1'),
    ]
    const all = [...vms, host('node2')]

    expect(outline(buildTree(vms, [{ kind: 'hypervisor' }], all))).toEqual([
      'A host you can’t see (1)',
      ['orphan'],
      // node1 runs on its own hardware, so it heads its group instead of being "not on a known host".
      'node1 (3)',
      ['node1', 'app01', 'app02'],
      // Named even though the search hides node2 itself.
      'node2 (1)',
      ['db01'],
    ])
    expect(outline(buildTree(vms.slice(0, 2), [{ kind: 'hypervisor' }, { kind: 'tag', name: 'site' }], all))).toEqual([
      'node1 (2)',
      ['DC-A (1)', ['app01'], 'DC-B (1)', ['app02']],
    ])
  })

  it('shows a nested hypervisor under the host it runs on and at the top of its own group', () => {
    const hosts = [
      host('metal'),
      host('pve-vm', [], { isVirtualMachine: true, hypervisorId: 'metal' }),
      host('guest', [], { isVirtualMachine: true, hypervisorId: 'pve-vm' }),
      host('printer'),
      host('stray', [], { isVirtualMachine: true }),
    ]

    expect(outline(buildTree(hosts, [{ kind: 'hypervisor' }]))).toEqual([
      'metal (2)',
      ['metal', 'pve-vm'],
      // Physical: it runs on its own hardware.
      'printer (1)',
      ['printer'],
      'pve-vm (2)',
      ['pve-vm', 'guest'],
      'Not on a known host (1)',
      ['stray'],
    ])
  })
})

describe('sortHosts', () => {
  it('sorts names case-insensitively and numerically', () => {
    const names = sortHosts([host('srv10'), host('Srv2'), host('alpha')], 'name').map((h) => h.name)
    expect(names).toEqual(['alpha', 'Srv2', 'srv10'])
  })

  it('sorts by address with IPs in numeric order', () => {
    const sorted = sortHosts(
      [host('a', [], { hostname: null, ipAddress: '10.0.0.15' }), host('b', [], { hostname: null, ipAddress: '10.0.0.2' })],
      'address',
    )
    expect(sorted.map((h) => h.name)).toEqual(['b', 'a'])
  })
})

describe('search', () => {
  const sql = host('sql01', [t('env', 'production'), t('site', 'Datacenter A')], {
    description: 'Primary database',
    protocols: ['Ssms'],
  })

  it('requires every term to match', () => {
    expect(matchesSearch(sql, parseSearch('SQL primary'))).toBe(true)
    expect(matchesSearch(sql, parseSearch('sql missing'))).toBe(false)
  })

  it('matches name:value tag terms, including quoted values and name-only', () => {
    expect(matchesSearch(sql, parseSearch('env:prod'))).toBe(true)
    expect(matchesSearch(sql, parseSearch('env:staging'))).toBe(false)
    expect(matchesSearch(sql, parseSearch('site:"datacenter a"'))).toBe(true)
    expect(matchesSearch(sql, parseSearch('env:'))).toBe(true)
    expect(matchesSearch(sql, parseSearch('role:'))).toBe(false)
  })

  it('searches services and tag values as free text', () => {
    expect(matchesSearch(sql, parseSearch('ssms'))).toBe(true)
    expect(matchesSearch(sql, parseSearch('"datacenter a"'))).toBe(true)
  })

  it('builds tag terms that round-trip through the parser', () => {
    expect(tagSearchTerm('site', 'Datacenter A')).toBe('site:"Datacenter A"')
    expect(parseSearch(tagSearchTerm('site', 'Datacenter A'))).toEqual([{ kind: 'tag', name: 'site', value: 'datacenter a' }])
    expect(tagSearchTerm('backup', null)).toBe('backup:')
  })
})

describe('tagNames', () => {
  it('lists distinct names, first spelling wins', () => {
    expect(tagNames([host('a', [t('Site', 'x'), t('env')]), host('b', [t('site', 'y')])])).toEqual(['env', 'Site'])
  })
})

describe('primaryAddress', () => {
  it('is the IP address when one is set, as connections use it', () => {
    expect(primaryAddress({ hostname: 'sql01.corp.example.com', ipAddress: '10.0.0.15' })).toBe('10.0.0.15')
    expect(primaryAddress({ hostname: 'sql01.corp.example.com', ipAddress: null })).toBe('sql01.corp.example.com')
    expect(primaryAddress({ hostname: null, ipAddress: null })).toBe('')
  })
})
