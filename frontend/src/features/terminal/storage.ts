import type { HostKeyPrompt } from './kvm'

/** An iSCSI session: the target, the portal it's logged in through (without the portal group), its state and attached disks. */
export interface IscsiSession {
  target: string
  /** The portal it was logged in through (its node record's). */
  portal: string
  /** Where a SAN that redirects logins (Nimble, EqualLogic…) actually connected it. */
  connectedTo?: string[] | null
  /** e.g. "LOGGED_IN", "FAILED". */
  state: string
  disks: string[]
}

/** A target the initiator knows (from discovery), and whether it's logged in. */
export interface IscsiNode {
  target: string
  portal: string
  loggedIn: boolean
}

export interface MultipathPath {
  /** host:channel:id:lun */
  address: string
  device: string
  /** "active" or "failed" (device-mapper). */
  dmState: string
  /** "ready", "ghost", "faulty"… (multipath's checker). */
  pathState: string
  /** "running", "offline"… (the kernel). */
  onlineState: string
}

export interface MultipathMap {
  /** Its alias, or its WWID when it has none. */
  name: string
  wwid: string
  dmDevice: string
  product: string | null
  size: string | null
  paths: MultipathPath[]
}

export interface DrbdResource {
  name: string
  up: boolean
  /** This node's role; null when the resource is down. */
  role: string | null
  volumes: { volume: number; minor: number | null; diskState: string | null }[]
  peers: {
    name: string
    connectionState: string
    role: string | null
    volumes: { volume: number; replicationState: string | null; diskState: string | null; percentInSync: number | null }[]
  }[]
}

/** What's on a multipath map, as Proxmox LVM storage sees it. */
export interface LvmMap {
  map: string
  /** The LVM volume group it's a physical volume of. */
  volumeGroup: string | null
  /** What else is on it (a file system, a partition table), when it isn't a volume group. */
  signature: string | null
  /** The Proxmox LVM storage on its volume group, on any node. */
  storage: string | null
  /** That storage is enabled on this node. */
  mounted: boolean
}

/** A Proxmox node's LVM storage on its multipath maps. */
export interface ProxmoxLvm {
  node: string
  clusterNodes: string[]
  allStorage: string[]
  storages: { id: string; volumeGroup: string; nodes: string[]; disabled: boolean }[]
  maps: LvmMap[]
}

/** A host's storage; each part is null when its tools aren't installed (proxmox: when it isn't a Proxmox node). */
export interface StorageStatus {
  iscsi: { initiatorName: string | null; sessions: IscsiSession[]; nodes: IscsiNode[] } | null
  multipath: MultipathMap[] | null
  drbd: DrbdResource[] | null
  proxmox?: ProxmoxLvm | null
}

export interface StorageResponse {
  status: 'ready' | 'trustRequired'
  storage: StorageStatus | null
  hostKey: HostKeyPrompt | null
  /** What an action did, when the new state doesn't show it (e.g. what discovery found). */
  message?: string | null
}

export type StorageAction =
  | 'iscsi.discover'
  | 'iscsi.login'
  | 'iscsi.logout'
  | 'iscsi.rescan'
  | 'multipath.reload'
  | 'multipath.flush'
  | 'drbd.up'
  | 'drbd.down'
  | 'drbd.primary'
  | 'drbd.secondary'
  | 'drbd.connect'
  | 'drbd.disconnect'
  | 'drbd.adjust'
  | 'lvm.mount'
  | 'lvm.unmount'

/** An action and what it acts on: an iSCSI target and portal, or a multipath map or DRBD resource. */
export interface StorageActionRequest {
  action: StorageAction
  target?: string
  portal?: string
  name?: string
  /** Mounting a map whose volume group has no Proxmox storage: the new storage's ID. */
  storage?: string
}

/**
 * Actions that take storage away from whatever uses it, or change which node may write: they ask
 * first, saying what happens.
 */
export const CONFIRM: Partial<Record<StorageAction, { label: string; explanation: string }>> = {
  'iscsi.logout': { label: 'Log out', explanation: 'Its disks disappear from this host. Anything using them (mounts, LVM, multipath, VMs) loses them.' },
  'multipath.flush': { label: 'Flush', explanation: 'Removes the multipath device. Multipath refuses if it is still in use.' },
  'drbd.down': { label: 'Take down', explanation: 'Stops replicating and detaches its disk. Its device disappears on this node.' },
  'drbd.primary': { label: 'Make primary', explanation: 'This node may then write to it. DRBD refuses if a peer is already primary (unless the resource allows two).' },
  'drbd.secondary': { label: 'Make secondary', explanation: 'This node stops writing to it. DRBD refuses while it is mounted or open.' },
  'drbd.disconnect': { label: 'Disconnect', explanation: 'Stops replicating to its peers until it is connected again. Changes made meanwhile are resynced then.' },
  'lvm.unmount': {
    label: 'Unmount',
    explanation:
      'Proxmox stops using its LVM storage on this node, and its volume group is deactivated here. Its data stays. NKeeper refuses while a guest on this node has disks on it. The storage is removed when no other node uses it.',
  },
}

/** One row per known target and portal, with its session when logged in; sessions without a node record too. */
export function iscsiRows(status: NonNullable<StorageStatus['iscsi']>) {
  const rows = status.nodes.map((node) => ({
    target: node.target,
    portal: node.portal,
    session: status.sessions.find((s) => s.target === node.target && s.portal === node.portal) ?? null,
  }))
  for (const session of status.sessions) {
    if (!rows.some((r) => r.session === session)) {
      rows.push({ target: session.target, portal: session.portal, session })
    }
  }
  return rows
}

/**
 * The iSCSI targets a multipath map's paths come from: the sessions that attached its path
 * devices (e.g. sdb), with the portals they reach them through. Empty for local or FC disks.
 */
export function multipathTargets(map: MultipathMap, iscsi: StorageStatus['iscsi']): { target: string; portals: string[] }[] {
  const devices = new Set(map.paths.map((p) => p.device))
  const targets = new Map<string, string[]>()
  for (const session of iscsi?.sessions ?? []) {
    if (session.disks.some((d) => devices.has(d))) {
      targets.set(session.target, [...(targets.get(session.target) ?? []), session.portal])
    }
  }
  return [...targets].map(([target, portals]) => ({ target, portals }))
}

/** The maps an iSCSI session's disks are paths of. */
export function sessionMaps(session: IscsiSession, multipath: StorageStatus['multipath']): MultipathMap[] {
  return (multipath ?? []).filter((m) => m.paths.some((p) => session.disks.includes(p.device)))
}

/** A map Proxmox uses here that would lose its last path if this session logged out. */
export function lastPathToMounted(session: IscsiSession, storage: StorageStatus): MultipathMap | null {
  const others = new Set(storage.iscsi?.sessions.filter((s) => s !== session && s.state === 'LOGGED_IN').flatMap((s) => s.disks) ?? [])
  return (
    sessionMaps(session, storage.multipath).find(
      (m) => storage.proxmox?.maps.some((l) => l.map === m.name && l.mounted) && !m.paths.some((p) => others.has(p.device)),
    ) ?? null
  )
}

/** A Proxmox storage ID for a map: its volume group's name, else its iSCSI target's last part, else its own name. */
export function suggestedStorageId(lvm: LvmMap, map: MultipathMap | undefined, iscsi: StorageStatus['iscsi']): string {
  const target = map ? multipathTargets(map, iscsi)[0]?.target : undefined
  const raw = lvm.volumeGroup ?? target?.split(':').pop() ?? lvm.map
  const id = raw.replace(/[^A-Za-z0-9._-]/g, '-').replace(/^[^A-Za-z]+/, '').replace(/[^A-Za-z0-9]+$/, '')
  return id.length >= 2 ? id.slice(0, 64) : `san-${id || lvm.map}`.slice(0, 64)
}
