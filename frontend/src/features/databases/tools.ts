/** How a Management tool opens: a report page, or a page of its own. */
type ToolView = 'report' | 'blocking' | 'orphans' | 'copy'

interface ToolInfo {
  /** Its tab's title and heading. */
  title: string
  /** The line under a report's heading. */
  subtitle?: string
  view: ToolView
}

/**
 * Every Management tool, by the name the server's explorer gives it. The server decides which an engine has
 * (its explorer lists them); this says how each looks and opens. A tool the server offers that isn't here
 * means the page is older than the server.
 */
export const TOOLS = {
  blocking: { title: 'Blocking Manager', view: 'blocking' },
  orphans: { title: 'Orphaned Users', view: 'orphans' },
  copy: { title: 'Copy Database', view: 'copy' },
  replication: { title: 'Replication Health', subtitle: 'Logs, Rep Agents, Threads and Queues', view: 'report' },
  // Oracle
  storage: { title: 'Storage', subtitle: 'Tablespaces and Datafiles', view: 'report' },
  backups: { title: 'Backups (RMAN)', subtitle: 'Backup History and Recovery Area', view: 'report' },
  health: { title: 'Health', subtitle: 'Limits, Alerts and Invalid Objects', view: 'report' },
  // SAP ASE
  space: { title: 'Space', subtitle: 'Databases, Logs and Devices', view: 'report' },
  drift: { title: 'Replication Drift', subtitle: 'Last Applied, Latency and Exceptions', view: 'report' },
  dumps: { title: 'Backups (dumps)', subtitle: 'Last Full and Log Dumps', view: 'report' },
  errors: { title: 'Error Log', subtitle: 'Recent Errors and Deadlocks', view: 'report' },
  resources: { title: 'Resources & Activity', subtitle: 'Limits, Transactions, Sessions and Caches', view: 'report' },
  security: { title: 'Logins & Users', subtitle: 'Locked Logins and Users Without Logins', view: 'report' },
  maintenance: { title: 'Maintenance', subtitle: 'Largest Tables and Their Statistics', view: 'report' },
  // SAP ASE and Replication Server
  license: { title: 'License', subtitle: 'Licenses and Expiry', view: 'report' },
  // Replication Server
  admin: { title: 'Admin Commands', subtitle: 'Threads, Queues, Connections and Versions', view: 'report' },
} as const satisfies Record<string, ToolInfo>

export type ToolKind = keyof typeof TOOLS

/** The tools that are report pages (served by the reports endpoint). */
export type ReportTool = { [K in ToolKind]: (typeof TOOLS)[K]['view'] extends 'report' ? K : never }[ToolKind]

export function isTool(name: string): name is ToolKind {
  return Object.hasOwn(TOOLS, name)
}

export function isReport(tool: ToolKind): tool is ReportTool {
  return TOOLS[tool].view === 'report'
}
