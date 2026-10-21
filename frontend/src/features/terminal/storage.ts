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

/** A host's storage; each part is null when its tools aren't installed. */
export interface StorageStatus {
  iscsi: { initiatorName: string | null; sessions: IscsiSession[]; nodes: IscsiNode[] } | null
  multipath: MultipathMap[] | null
  drbd: DrbdResource[] | null
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

/** An action and what it acts on: an iSCSI target and portal, or a multipath map or DRBD resource. */
export interface StorageActionRequest {
  action: StorageAction
  target?: string
  portal?: string
  name?: string
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
