/** Protocols NKeeper opens sessions for. */
export type SessionProtocol = 'ssh' | 'rdp' | 'web'

/** What the browser asks the server to connect to. Secrets for saved credentials stay on the server. */
export interface SessionRequest {
  hostId: string
  /** Index into the host's ordered services. */
  serviceIndex: number
  credentialId?: string
  /** One-off login typed into the prompt; sent once and then dropped from memory. */
  oneOff?: { username: string; password: string; domain?: string }
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
