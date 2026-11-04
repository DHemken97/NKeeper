import { api } from '../../lib/api'
import type { CertificatePrompt } from '../terminal/kvm'
import type { SessionRequest } from '../terminal/types'

/** What a database server says about itself when a session opens. */
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
  /** Oracle's serial#: with the SID, it names the session to kill. */
  serial?: number | null
}

export interface BlockingReport {
  leads: BlockingSession[]
  blockedCount: number
  canSeeAllSessions: boolean
  atUtc: string
}

/** A database user whose login is gone or no longer has its SID (e.g. after a restore). */
export interface OrphanedUser {
  database: string
  user: string
  type: 'SQL user' | 'Windows user' | 'Windows group'
  /** 0x… : a login created with it maps to the user. */
  sid: string
  defaultSchema: string | null
  /** A login of the same name and kind, to map the user to. */
  matchingLogin: string | null
  /** Schemas it owns: it can't be dropped while it owns any. */
  ownedSchemas: string[]
}

export interface OrphanReport {
  users: OrphanedUser[]
  /** Databases whose owner isn't a login here. */
  owners: { database: string; ownerSid: string }[]
  logins: { name: string; windows: boolean; disabled: boolean }[]
  /** Databases that couldn't be checked, with why. */
  skipped: string[]
  databasesChecked: number
}

export type OrphanFix =
  | { action: 'map'; database: string; user: string; login?: string }
  | { action: 'create'; database: string; user: string; login?: string; password?: string; checkPolicy?: boolean }
  | { action: 'drop'; database: string; user: string }
  | { action: 'owner'; database: string; login: string }

/** A script a report offers: SQL opens in a query window; "copy" text runs elsewhere (RMAN, SQL*Plus on the server). */
export interface ToolScript {
  label: string
  text: string
  kind: 'sql' | 'copy'
}

/** What one command of a Management report said: its result sets and messages, or why it failed; and scripts to act on it. */
export interface ToolSection {
  title: string
  command: string
  results: SqlOutput[]
  error: string | null
  scripts?: ToolScript[] | null
  note?: string | null
}

/** A suspended or down DSI connection on a Replication Server. */
export interface SuspendedConnection {
  /** server.database */
  connection: string
  thread: string
  state: string
}

export interface ToolReport {
  /** What needs looking at; empty when all is well. */
  warnings: string[]
  suspended: SuspendedConnection[]
  sections: ToolSection[]
  atUtc: string
}

/** One of the user's open sessions, to pick another server. */
export interface DatabaseSessionSummary {
  id: string
  display: string
  hostId: string
  serverName: string
  version: string
}

export interface AseCopyServer {
  server: string
  version: string
  pageSize: number
  charsetId: number | null
  sortOrderId: number | null
}

/** What copying a database needs, and what stands in its way. */
export interface AseCopyPlan {
  source: AseCopyServer
  target: AseCopyServer
  dataMb: number
  logMb: number
  targetExists: boolean
  /** Reasons it can't work as it is. */
  blockers: string[]
  warnings: string[]
  /** When the target doesn't exist: a CREATE DATABASE … FOR LOAD big enough. */
  createSql: string | null
  /** Servers the source knows, to dump through the target's Backup Server. */
  backupServers: string[]
  /** The source holds a replication truncation point. */
  replicated: boolean
}

export type AseCopyStep = 'create' | 'dump' | 'load' | 'online' | 'settrunc'

export interface AseCopyStepRequest {
  targetSessionId: string
  database: string
  targetDatabase: string
  step: AseCopyStep
  dumpPath?: string
  loadPath?: string
  remoteBackupServer?: string
  compress?: boolean
  createSql?: string
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

/** The database engines the explorer works with, by the API path they're under. */
export type DbEngine = 'mssql' | 'oracle' | 'ase' | 'rs'

/** The Management tools, by the name the explorer gives them. */
export type ToolKind = 'blocking' | 'orphans' | 'replication' | 'copy' | OracleReport | AseReport | RepServerReport

/** Oracle's read-only Management reports. */
export type OracleReport = 'storage' | 'backups' | 'health'

/** SAP ASE's read-only Management reports. */
export type AseReport = 'space' | 'drift' | 'license' | 'dumps' | 'errors' | 'resources' | 'security' | 'maintenance'

/** Replication Server's read-only Management reports. */
export type RepServerReport = 'admin' | 'license'

export interface DbEngineInfo {
  /** e.g. "SQL Server": the product, as the tab and messages name it. */
  product: string
  /** What a query window's database list holds: Oracle switches schemas; null when there's none (Replication Server). */
  databaseLabel: string | null
  /** The Management tools this engine has. */
  tools: ToolKind[]
}

export const DB_ENGINES: Record<DbEngine, DbEngineInfo> = {
  mssql: { product: 'SQL Server', databaseLabel: 'Database', tools: ['blocking', 'orphans'] },
  oracle: { product: 'Oracle', databaseLabel: 'Schema', tools: ['blocking', 'storage', 'backups', 'health'] },
  ase: { product: 'SAP ASE', databaseLabel: 'Database', tools: ['blocking', 'replication', 'space', 'drift', 'copy', 'license', 'dumps', 'errors', 'resources', 'security', 'maintenance'] },
  rs: { product: 'Replication Server', databaseLabel: null, tools: ['replication', 'admin', 'license'] },
}

function createDbApi(engine: DbEngine) {
  const root = `/api/${engine}/sessions`
  const base = (sessionId: string) => `${root}/${encodeURIComponent(sessionId)}`
  return {
    engine,
    /** The session's path, e.g. to close it when the page goes away. */
    sessionPath: base,
    open: (request: SessionRequest, trustFingerprint?: string, database?: string) =>
      api.post<SqlOpenResponse>(root, { body: openRequestBody(request, trustFingerprint, database) }),
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
    orphans: (sessionId: string) => api.get<OrphanReport>(`${base(sessionId)}/orphans`),
    fixOrphan: (sessionId: string, fix: OrphanFix) => api.post<{ report: OrphanReport; message: string }>(`${base(sessionId)}/orphans/fix`, { body: fix }),
    fixManyOrphans: (sessionId: string, fixes: { action: 'map' | 'drop'; database: string; user: string }[]) =>
      api.post<{ report: OrphanReport; message: string; results: { database: string; user: string; succeeded: boolean; message: string }[] }>(
        `${base(sessionId)}/orphans/fix-many`,
        { body: { fixes } },
      ),
    fixMatchedOrphans: (sessionId: string) => api.post<{ report: OrphanReport; message: string }>(`${base(sessionId)}/orphans/fix-matched`),
    /** Oracle: Storage, Backups or Health; ASE: Space, Replication Drift or License; Replication Server: Admin Commands or License. */
    report: (sessionId: string, tool: Exclude<ToolKind, 'blocking' | 'orphans' | 'copy'>) => api.get<ToolReport>(`${base(sessionId)}/reports/${tool}`),
    /** Replication Server: resume connection to server.database (optionally skipping the failed transaction). Audited. */
    resumeConnection: (sessionId: string, connection: string, skipTransaction: boolean) =>
      api.post<void>(`${base(sessionId)}/replication/resume`, { body: { connection, skipTransaction } }),
    /** The user's open sessions of this engine, one per server and login (to compare with or copy to). */
    sessions: () => api.get<DatabaseSessionSummary[]>(root),
    /** ASE: row counts of replicated tables here and on another server. */
    compare: (sessionId: string, body: { otherSessionId: string; database: string; otherDatabase?: string; exact: boolean }) =>
      api.post<ToolReport>(`${base(sessionId)}/drift/compare`, { body }),
    copyCheck: (sessionId: string, body: { targetSessionId: string; database: string; targetDatabase: string }) =>
      api.post<AseCopyPlan>(`${base(sessionId)}/copy/check`, { body }),
    /** Runs one step of a copy (for as long as it takes). Audited. */
    copyRun: (sessionId: string, body: AseCopyStepRequest) => api.post<ToolSection>(`${base(sessionId)}/copy/run`, { body }),
    /** Ends a session; Oracle also needs its serial#. */
    kill: (sessionId: string, spid: number, serial?: number | null) =>
      api.post<void>(`${base(sessionId)}/kill`, { body: serial == null ? { sessionId: spid } : { sessionId: spid, serial } }),
  }
}

export type DbApi = ReturnType<typeof createDbApi>

const apis = new Map<DbEngine, DbApi>()

/** The API for an engine (the same object each time). */
export function dbApi(engine: DbEngine): DbApi {
  let found = apis.get(engine)
  if (!found) {
    found = createDbApi(engine)
    apis.set(engine, found)
  }
  return found
}

export const sqlServerApi = dbApi('mssql')

/** The scripts the explorer offers for a node, as its menu labels them. */
export function scriptsFor(kind: string, engine: DbEngine = 'mssql'): { action: SqlScriptAction; label: string }[] {
  const selectable = engine === 'ase' ? ['table', 'view'] : ['table', 'view', 'synonym']
  const creatable = ['table', 'view', 'procedure', 'function', 'trigger', ...(engine === 'oracle' ? ['package', 'sequence', 'synonym'] : [])]
  return [
    ...(selectable.includes(kind) ? [{ action: 'selectTop' as const, label: 'Select Top 1000 Rows' }] : []),
    ...(creatable.includes(kind) ? [{ action: 'create' as const, label: 'Script as CREATE' }] : []),
    ...(kind === 'procedure' ? [{ action: 'execute' as const, label: 'Script as EXECUTE' }] : []),
  ]
}
