/** What the browser asks the server to connect to. Secrets for saved credentials stay on the server. */
export interface SshSessionRequest {
  hostId: string
  /** Index into the host's ordered services. */
  serviceIndex: number
  credentialId?: string
  /** One-off login typed into the prompt; sent once and then dropped from memory. */
  oneOff?: { username: string; password: string }
}

/** An open terminal tab. */
export interface TerminalSession {
  id: string
  hostId: string
  /** Tab title, e.g. "sql01". */
  title: string
  /** e.g. "SSH · root". */
  detail: string
  request: SshSessionRequest
}

/** Control messages from the server (see backend SshProtocol.cs). */
export type ServerMessage =
  | { type: 'status'; status: 'connecting'; target: string }
  | { type: 'connected'; target: string }
  | {
      type: 'hostKey'
      status: 'unknown' | 'changed'
      port: number
      algorithm: string
      fingerprint: string
      trustedFingerprint?: string
    }
  | { type: 'error'; code: string; message: string }
  | { type: 'closed'; message: string }
