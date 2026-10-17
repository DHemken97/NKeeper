/**
 * Protocols NKeeper opens sessions for. kvm: a hypervisor's VM list; spice and vmvnc: one VM's
 * console (vnc is a VNC server reached through guacd); idrac: a server's status and power over
 * Redfish; idracconsole: its iDRAC's HTML5 virtual console (RACADM is an ssh session).
 */
export type SessionProtocol = 'ssh' | 'telnet' | 'sftp' | 'rdp' | 'rpc' | 'vnc' | 'web' | 'kvm' | 'spice' | 'vmvnc' | 'idrac' | 'idracconsole'

/** What the browser asks the server to connect to. Secrets for saved credentials stay on the server. */
export interface SessionRequest {
  hostId: string
  /** Index into the host's ordered services. */
  serviceIndex: number
  credentialId?: string
  /**
   * One-off login typed into the prompt. SSH/RDP/VNC send it once and drop it; a KVM tab keeps it
   * (in memory, for the tab's lifetime) to open VM consoles.
   */
  oneOff?: { username: string; password: string; domain?: string }
  /** RDP: log in as the user's own domain account, which they signed in to NKeeper with. The password stays on the server. */
  useSignIn?: boolean
  /** For VM consoles: the virtual machine on the KVM host. */
  vm?: { uuid: string; name: string }
}

/** An open session tab. */
export interface RemoteSession {
  id: string
  hostId: string
  protocol: SessionProtocol
  /** Tab title, e.g. "sql01". */
  title: string
  /** e.g. "SSH · root". */
  detail: string
  request: SessionRequest
}

/** The server's identity, shown for trust on first use (an SSH host key or an RDP certificate). */
export interface HostKeyMessage {
  type: 'hostKey'
  status: 'unknown' | 'changed'
  kind: 'ssh' | 'certificate'
  port: number
  algorithm: string
  fingerprint: string
  trustedFingerprint?: string
  /** Certificates only. */
  subject?: string
  expires?: string
}

/** Control messages from the server (see backend RemoteSessions/SessionProtocol.cs). */
export type ServerMessage =
  | { type: 'status'; status: 'connecting'; target: string }
  | { type: 'connected'; target: string }
  | HostKeyMessage
  | { type: 'error'; code: string; message: string }
  | { type: 'closed'; message: string }

/** Where a session is in its life; shared by the SSH and RDP views. */
export type SessionPhase =
  | { kind: 'connecting'; target?: string }
  | { kind: 'hostKey'; prompt: HostKeyMessage; target?: string }
  | { kind: 'connected'; target: string }
  /** Ended with an error; a normal end closes the tab instead. */
  | { kind: 'ended'; message: string; code?: string; target?: string }
