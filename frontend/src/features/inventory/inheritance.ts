import type { Host, HostTag, InheritedTag } from './types'

/**
 * The tags a host inherits: those of the host it runs on (and that host's own hypervisor, and so
 * on) that are inheritable and whose name the host, or a hypervisor nearer to it, doesn't have.
 * Worked out from the hosts the user can see, so a hypervisor they can't see passes nothing down.
 */
export function inheritedTags(own: HostTag[], hypervisorId: string | null, byId: ReadonlyMap<string, Host>, selfId?: string): InheritedTag[] {
  const names = new Set(own.map((t) => t.name.toLowerCase()))
  const seen = new Set(selfId ? [selfId] : [])
  const inherited: InheritedTag[] = []
  for (let parent = hypervisorId ? byId.get(hypervisorId) : undefined; parent && !seen.has(parent.id); parent = parent.hypervisorId ? byId.get(parent.hypervisorId) : undefined) {
    seen.add(parent.id)
    for (const tag of parent.tags) {
      if (tag.inheritable !== false && !names.has(tag.name.toLowerCase())) {
        inherited.push({ name: tag.name, value: tag.value, fromHostId: parent.id, fromHostName: parent.name })
      }
    }
    // A name a nearer host has, inherited or not, hides that name further up.
    parent.tags.forEach((t) => names.add(t.name.toLowerCase()))
  }
  return inherited
}

/** Every host with the tags it inherits filled in. */
export function withInheritedTags(hosts: Host[]): Host[] {
  const byId = new Map(hosts.map((h) => [h.id, h]))
  return hosts.map((host) => ({ ...host, inheritedTags: inheritedTags(host.tags, host.isVirtualMachine ? host.hypervisorId : null, byId, host.id) }))
}

/** A host's own tags, then the ones it inherits: what search, grouping and reports go by. */
export function effectiveTags(host: Host): HostTag[] {
  return host.inheritedTags?.length ? [...host.tags, ...host.inheritedTags] : host.tags
}
