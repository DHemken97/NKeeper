import { api } from '../../lib/api'
import type { CertificatePrompt } from '../terminal/kvm'
import type { SessionRequest } from '../terminal/types'

/** What a SQL Server says about itself when a session opens. */
export interface SqlServerInfo {
  serverName: string
  productVersion: string
  productLevel: string | null
  edition: string | null
  login: string
  /** The session's own SPID. */
  sessionId: number
}

export interface SqlOpenResponse {
  status: 'ready' | 'trustRequired'
  sessionId: string | null
  /** e.g. "sa@10.0.0.5\SQLEXPRESS:1433". */
  target: string
  server: SqlServerInfo | null
  database: string | null
  certificate: CertificatePrompt | null
  /** The certificate replaces a trusted one: SQL Server made itself a new self-signed one (it does at every start). */
  renewed?: boolean
}

export interface SqlColumn {
  name: string
  type: string
  numeric: boolean
}

/** One thing a query produced, in order: a result set, "(n rows affected)", a message or an error. */
export interface SqlOutput {
  kind: 'results' | 'rows' | 'message' | 'error'
  text?: string | null
  columns?: SqlColumn[] | null
  rows?: (string | null)[][] | null
  /** Every row of a result set, including those past the ones sent; the count for "rows". */
  rowCount?: number | null
  /** More rows than were sent. */
  truncated?: boolean
  number?: number | null
  severity?: number | null
  state?: number | null
  procedure?: string | null
  /** The editor's line the error is on. */
  line?: number | null
}

export interface SqlQueryResult {
  output: SqlOutput[]
  database: string
  elapsedMs: number
  cancelled: boolean
  /** The connection broke: the window must connect again. */
  connectionLost: boolean
  hasErrors: boolean
}

/** A node of the object explorer; sent back as it is to list its children or script it. */
export interface SqlObjectNode {
  kind: string
  label: string
  detail?: string | null
  expandable: boolean
  database?: string | null
  schema?: string | null
  name?: string | null
  objectId?: number | null
}

export type SqlScriptAction = 'selectTop' | 'create' | 'execute'

/** A session in a blocking chain, with the sessions it blocks under it. */
export interface BlockingSession {
  sessionId: number
  login: string | null
  host: string | null
  program: string | null
  /** Null when it isn't in a database (N/A). */
  database: string | null
  status: string | null
  openTransactions: number
  blockedBy: number | null
  command: string | null
  waitType: string | null
  waitTimeMs: number | null
  waitResource: string | null
  sql: string | null
  /** Sessions it blocks directly. */
  blocked: number
  /** Every session waiting behind it, down the chain. */
  blockedVolume: number
  blockedSessions: BlockingSession[]
  /** A blocker that isn't a session (e.g. -2, an orphaned distributed transaction): it can't be killed. */
  placeholder?: boolean
}

export interface BlockingReport {
  leads: BlockingSession[]
  blockedCount: number
  canSeeAllSessions: boolean
  atUtc: string
}

/** The body that opens a session: the tab's host, service and login, a certificate the user trusted, and a database to start in. */
export function openRequestBody(request: SessionRequest, trustFingerprint?: string, database?: string) {
  return {
    hostId: request.hostId,
    serviceIndex: request.serviceIndex,
    credentialId: request.credentialId,
    username: request.oneOff?.username,
    password: request.oneOff?.password,
    trustFingerprint,
    database,
  }
}

const base = (sessionId: string) => `/api/mssql/sessions/${encodeURIComponent(sessionId)}`

export const sqlServerApi = {
  open: (request: SessionRequest, trustFingerprint?: string, database?: string) =>
    api.post<SqlOpenResponse>('/api/mssql/sessions', { body: openRequestBody(request, trustFingerprint, database) }),
  close: (sessionId: string) => api.delete<void>(base(sessionId)),
  keepAlive: (sessionId: string) => api.post<void>(`${base(sessionId)}/keepalive`),
  query: (sessionId: string, sql: string, lineOffset: number) => api.post<SqlQueryResult>(`${base(sessionId)}/query`, { body: { sql, lineOffset } }),
  cancel: (sessionId: string) => api.post<void>(`${base(sessionId)}/cancel`),
  databases: (sessionId: string) => api.get<string[]>(`${base(sessionId)}/databases`),
  changeDatabase: (sessionId: string, name: string) => api.post<{ name: string }>(`${base(sessionId)}/database`, { body: { name } }),
  explore: (sessionId: string, node: SqlObjectNode) => api.post<SqlObjectNode[]>(`${base(sessionId)}/explore`, { body: { node } }),
  script: (sessionId: string, node: SqlObjectNode, action: SqlScriptAction) =>
    api.post<{ sql: string }>(`${base(sessionId)}/script`, { body: { node, action } }),
  blocking: (sessionId: string) => api.get<BlockingReport>(`${base(sessionId)}/blocking`),
  kill: (sessionId: string, spid: number) => api.post<void>(`${base(sessionId)}/kill`, { body: { sessionId: spid } }),
}

/** The scripts the explorer offers for a node, as its menu labels them. */
export function scriptsFor(kind: string): { action: SqlScriptAction; label: string }[] {
  return [
    ...(['table', 'view', 'synonym'].includes(kind) ? [{ action: 'selectTop' as const, label: 'Select Top 1000 Rows' }] : []),
    ...(['table', 'view', 'procedure', 'function', 'trigger'].includes(kind) ? [{ action: 'create' as const, label: 'Script as CREATE' }] : []),
    ...(kind === 'procedure' ? [{ action: 'execute' as const, label: 'Script as EXECUTE' }] : []),
  ]
}
