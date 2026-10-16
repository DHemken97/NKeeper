import type { Host } from './types'

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' })

/**
 * Hosts to offer as "runs on", split into likely hypervisors (hosts that already run VMs, or have a
 * KVM service) and the rest, each sorted by name.
 */
export function hypervisorChoices(hosts: Host[]): { hypervisors: Host[]; others: Host[] } {
  const hypervisorIds = new Set(hosts.map((h) => h.hypervisorId).filter(Boolean))
  const isHypervisor = (h: Host) => !h.isVirtualMachine && (hypervisorIds.has(h.id) || h.services.some((s) => s.protocol === 'Kvm'))
  const byName = (a: Host, b: Host) => collator.compare(a.name, b.name)
  return { hypervisors: hosts.filter(isHypervisor).sort(byName), others: hosts.filter((h) => !isHypervisor(h)).sort(byName) }
}
