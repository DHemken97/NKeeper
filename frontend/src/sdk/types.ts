import type { ComponentType, ReactNode } from 'react'
import type { MenuEntry } from '../components/ui/ContextMenu'
import type { Host, HostCredential, ServiceProtocol } from '../features/inventory/types'
import type { RemoteSession, SessionRequest } from '../features/terminal/types'

/**
 * A plugin's browser part: what it adds to the web app. Every part is optional. Register it with
 * {@link definePlugin} from the plugin's entry script; the web app loads that script (signed, from
 * the plugin's package) after sign-in, for plugins the server has loaded.
 */
export interface NKeeperPlugin {
  /** The plugin's ID, as in its manifest: its API is under /api/plugins/{id}/. */
  id: string
  /**
   * Connect ▸ entries for services of these types, instead of NKeeper's own: an iDRAC's console
   * and status, a KVM host's virtual machines.
   */
  serviceMenus?: Partial<Record<ServiceProtocol, ServiceMenu>>
  /** More Connect ▸ entries for a host, after its services: a VM's console through its hypervisor, a host's storage. */
  connectEntries?: (host: Host, menu: HostMenuContext) => MenuEntry[]
  /** More Tasks ▸ entries for a host: a VM's power actions, migrating it. */
  taskEntries?: (host: Host, menu: HostMenuContext) => MenuEntry[]
  /** Views for session tabs, by name. A tab opened with {@link HostMenuContext.openSession} shows the view it names. */
  sessionViews?: Record<string, ComponentType<SessionViewProps>>
  /** Sections at the end of a host's details. */
  hostSections?: ComponentType<HostSectionProps>[]
  /** Fields in the host editor's details, for the plugin's per-host fields. */
  hostFields?: ComponentType<HostFieldProps>[]
  /** Sections of the Settings page (administrators only). */
  settingsSections?: ComponentType[]
  /** Reports, listed under Reports and shown at /reports/{path}. */
  reports?: PluginReport[]
}

/** A plugin's Connect ▸ entry for one type of service. */
export interface ServiceMenu {
  /** The service's entry, usually with a submenu (see {@link HostMenuContext.loginEntries}). */
  entry: (host: Host, serviceIndex: number, menu: HostMenuContext) => MenuEntry
  /**
   * What double-clicking the host opens when this is its first service, with the first usable
   * login. Without it, double-click passes over the service.
   */
  open?: (host: Host, serviceIndex: number, login: Login, menu: HostMenuContext) => void
}

/** A login for a session: a saved credential, or one typed into the prompt. */
export interface Login {
  credential?: HostCredential
  oneOff?: { username: string; password: string; domain?: string }
}

/** What the host menu offers plugins to build their entries with. */
export interface HostMenuContext {
  /** Every host the user can see. */
  hosts: Host[]
  /**
   * The service's logins: its host's saved credentials (asking for a password when none is
   * stored), then "Enter credentials…". Choosing one calls `onLogin`.
   */
  loginEntries: (host: Host, serviceIndex: number, onLogin: (login: Login) => void) => MenuEntry[]
  /** "Through NKeeper" and "Directly in a new tab" for a service's web interface. */
  webEntries: (host: Host, serviceIndex: number) => MenuEntry[]
  /** Opens a session tab. */
  openSession: (session: NewSession) => void
  /** Shows a dialog, until it calls `close`. */
  openDialog: (render: (close: () => void) => ReactNode) => void
  /** Fetches the hosts again (after a plugin changed some). */
  reloadHosts: () => void
}

/** A session tab to open. */
export interface NewSession {
  /** The host the tab belongs to (a VM's console belongs to the VM). */
  hostId: string
  /** The session view that shows it: one of NKeeper's (ssh, telnet, sftp, rdp, rpc, vnc, web) or a plugin's. */
  view: string
  title: string
  /** e.g. "iDRAC · Virtual console · root". */
  detail: string
  /** The host, service and login the view connects with. */
  request: SessionRequest
  /** Open tabs this one replaces (closed when it opens). */
  replaces?: (session: RemoteSession) => boolean
}

/** What a session view is given. */
export interface SessionViewProps {
  session: RemoteSession
  /** The tab is the one shown (and should take the keyboard). */
  active: boolean
  /** The host the tab belongs to, when the user can see it. */
  host: Host | undefined
  hosts: Host[]
  onShowHost: (hostId: string) => void
  /** Closes the tab. */
  onEnded: () => void
  /** The host changed (e.g. details copied onto it). */
  onHostChanged: (host: Host) => void
  /** Opens another tab, e.g. a VM's console from its hypervisor's VM list. */
  openSession: (session: NewSession) => void
  /** Changes this tab: another view, title or request (a VM's console once its display is known). */
  updateSession: (changes: Partial<Pick<RemoteSession, 'protocol' | 'title' | 'detail' | 'request'>>) => void
}

export interface HostSectionProps {
  host: Host
  onChanged: (host: Host) => void
}

/** What a host-editor field is given: the plugin's own per-host values, in the draft being edited. */
export interface HostFieldProps {
  /** The host as saved, or null for a new one. */
  host: Host | null
  /** The plugin's values in the draft, by field name; empty for none. */
  values: Record<string, string>
  onChange: (field: string, value: string) => void
  /** Errors for the plugin's fields, by field name. */
  errors: Record<string, string>
  /** What's typed so far, to suggest from. */
  draft: { name: string; hostname: string; ipAddress: string }
}

export interface PluginReport {
  /** The URL segment: /reports/{path}. */
  path: string
  label: string
  description: string
  adminOnly?: boolean
  component: ComponentType
}
