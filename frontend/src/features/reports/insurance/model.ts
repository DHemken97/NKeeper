import type { DatabaseSurvey, DbEngine, SurveyFact, SurveyTopic } from '../../databases/databaseApi'
import { effectiveTags } from '../../inventory/inheritance'
import { OS_TAG } from '../../inventory/scanProposals'
import { matchesSearch, parseSearch } from '../../inventory/search'
import { PROTOCOL_INFO, type Host, type HostService, type ServiceProtocol } from '../../inventory/types'

/*
 * The insurance questionnaire: what the insurer asks about servers, databases and backups, the answers
 * people type in (saved on the server so next year's renewal starts from this year's), and what NKeeper
 * found itself, from the inventory and from read-only surveys of the database servers.
 */

/** A question, the survey topics whose facts help answer it, and a hint of what a good answer says. */
export interface Question<K extends string> {
  key: K
  label: string
  hint?: string
  topics: SurveyTopic[]
}

export type DatabaseQuestionKey = 'software' | 'managedBy' | 'contents' | 'writers' | 'location' | 'deletion' | 'documentation'

export const DATABASE_QUESTIONS: Question<DatabaseQuestionKey>[] = [
  {
    key: 'software',
    label: 'Name, underlying software (and version) and developer',
    hint: 'Was the database (the application) developed internally or licensed from a third party?',
    topics: ['software'],
  },
  { key: 'managedBy', label: 'Group or individual that manages or maintains it', hint: 'e.g. “IT – database administration team”', topics: [] },
  { key: 'contents', label: 'Type of information it contains and what it’s used for', topics: ['contents'] },
  {
    key: 'writers',
    label: 'Groups or categories of users with rights to add, delete or alter data',
    hint: 'Groups and roles, not the names of individuals.',
    topics: ['writers'],
  },
  { key: 'location', label: 'Where it is stored', topics: ['location'] },
  {
    key: 'deletion',
    label: 'Routine or automatic deletion (schedule or parameters), and whether a log of each entry or change is kept',
    topics: ['deletion', 'changeLog'],
  },
  {
    key: 'documentation',
    label: 'Regular or standard reports, data dictionaries, diagrams, field trees, manuals and training materials available',
    topics: [],
  },
]

export type BackupQuestionKey = 'types' | 'data' | 'schedule' | 'retention' | 'labels' | 'singleRestore' | 'indexed' | 'media' | 'remote' | 'location'

export const BACKUP_QUESTIONS: Question<BackupQuestionKey>[] = [
  { key: 'types', label: 'Types of backups performed', hint: 'e.g. full, incremental, differential, transaction log', topics: ['backupTypes'] },
  { key: 'data', label: 'Types of data backed up', hint: 'e.g. e-mails, word-processing documents, databases', topics: ['backupData'] },
  { key: 'schedule', label: 'Backup schedule', hint: 'e.g. each business day, weekly, monthly', topics: ['backupSchedule'] },
  {
    key: 'retention',
    label: 'Period for which backups can be restored',
    hint: 'How many months or years backup media are kept before being overwritten.',
    topics: ['backupRetention'],
  },
  { key: 'labels', label: 'Whether backup media are labelled by type of data, or by the employees whose data they hold', topics: ['backupLabels'] },
  {
    key: 'singleRestore',
    label: 'Whether a single mailbox, or a single custodian’s files, can be restored without restoring the whole backup',
    topics: ['backupRestore'],
  },
  { key: 'indexed', label: 'Whether backup media are indexed or logged by the backup software', topics: ['backupIndex'] },
  { key: 'media', label: 'Type of media the backups are stored on, and the backup application (with version)', topics: ['backupMedia'] },
  { key: 'remote', label: 'Use of any remote or online backup service', topics: ['backupRemote'] },
  { key: 'location', label: 'Location of all backup media, on or off site', topics: ['backupLocation'] },
]

export type Answers<K extends string> = Partial<Record<K, string>>

/** A survey as it's saved: without its sections (they're big, and only the appendix uses them). */
export type SavedSurvey = Omit<DatabaseSurvey, 'sections'>

/** What's typed (and found) about one database server. */
export interface InstanceAnswers {
  /** Leave this server out of the report (e.g. a test server). */
  excluded?: boolean
  /** Answers that apply to every database on the server, unless a database has its own. */
  server?: Answers<DatabaseQuestionKey>
  /** Answers about one database, by its name. */
  databases?: Record<string, Answers<DatabaseQuestionKey>>
  backup?: Answers<BackupQuestionKey>
  survey?: SavedSurvey
  /** The credential the survey last used, to offer it again. */
  credentialId?: string
}

/** Backed-up data that isn't one of NKeeper's database servers: a file server, e-mail, Microsoft 365… */
export interface ExtraRepository {
  id: string
  name: string
  answers: Answers<BackupQuestionKey>
}

/** Everything the page saves (as JSON, on the server). */
export interface QuestionnaireAnswers {
  organisation?: string
  preparedBy?: string
  introduction?: string
  /** Hosts matching any of these comma-separated searches aren't counted as servers (printers, switches…). */
  excludeHosts?: string
  serverNotes?: string
  /** Answers for every database unless a server or database has its own. */
  databaseDefaults?: Answers<DatabaseQuestionKey>
  /** Answers for every repository unless it has its own. */
  backupDefaults?: Answers<BackupQuestionKey>
  /** By instance key (see instanceKey). */
  instances?: Record<string, InstanceAnswers>
  repositories?: ExtraRepository[]
  includeAppendix?: boolean
}

// ---- Servers ----

/** Tags that say where a host is, in the order they're read. */
const LOCATION_TAGS = ['site', 'location', 'datacenter', 'data center', 'dc', 'building', 'room', 'rack', 'region', 'cloud']

/** Tags that say what a host is for, counted when hosts have them. */
const KIND_TAGS = ['role', 'type', 'function', 'purpose', 'env', 'environment', 'site', 'location', 'datacenter']

/** Which of the hosts are counted as servers: all of them, less those matching the exclusions. */
export function countedHosts(hosts: Host[], exclude: string | undefined): Host[] {
  const queries = (exclude ?? '')
    .split(',')
    .map((q) => parseSearch(q.trim()))
    .filter((terms) => terms.length > 0)
  return queries.length === 0 ? hosts : hosts.filter((host) => !queries.some((terms) => matchesSearch(host, terms)))
}

export interface CountRow {
  label: string
  count: number
}

export interface ServerSummary {
  total: number
  physical: number
  virtual: number
  /** Physical hosts that run VMs NKeeper knows of. */
  hypervisors: number
  byOs: CountRow[]
  /** Counts by each "kind" tag the hosts have (role, env, site…). */
  byTag: { tag: string; rows: CountRow[] }[]
  /** Hosts offering each kind of service, e.g. "Database servers: Microsoft SQL Server". */
  byService: CountRow[]
  rows: ServerRow[]
}

export interface ServerRow {
  name: string
  address: string
  kind: string
  os: string
  location: string
  cpu: string
  memory: string
  serial: string
  services: string
}

function tagValue(host: Host, name: string): string | null {
  const tag = effectiveTags(host).find((t) => t.name.toLowerCase() === name)
  return tag ? (tag.value ?? '') : null
}

function count(values: string[]): CountRow[] {
  const counts = new Map<string, number>()
  values.forEach((v) => counts.set(v, (counts.get(v) ?? 0) + 1))
  return [...counts.entries()].map(([label, n]) => ({ label, count: n })).sort((a, b) => b.count - a.count || a.label.localeCompare(b.label))
}

/** The OS, or "Not recorded". */
export function hostOs(host: Host): string {
  return tagValue(host, OS_TAG) || 'Not recorded'
}

/** Where a host is, from its location tags and what it runs on. */
export function hostLocation(host: Host, byId: ReadonlyMap<string, Host>): string {
  const parts = LOCATION_TAGS.map((name) => {
    const value = tagValue(host, name)
    return value ? `${name}: ${value}` : null
  }).filter((p): p is string => p !== null)
  if (host.isVirtualMachine) {
    const hypervisor = host.hypervisorId ? byId.get(host.hypervisorId) : undefined
    parts.push(hypervisor ? `virtual machine on ${hypervisor.name}` : 'virtual machine')
  }
  return parts.join('; ')
}

export function hostAddress(host: Host): string {
  return [host.hostname, host.ipAddress].filter(Boolean).join(' / ')
}

/** What kind of server a service makes a host, for the counts. */
const SERVICE_ROLES: Partial<Record<ServiceProtocol, string>> = {
  Ssms: 'Database servers: Microsoft SQL Server',
  Oracle: 'Database servers: Oracle',
  Ase: 'Database servers: SAP ASE',
  RepServer: 'Replication servers: SAP Replication Server',
  Kvm: 'Virtualization hosts: KVM',
  Proxmox: 'Virtualization hosts: Proxmox VE',
  Http: 'Web servers and web interfaces',
  Https: 'Web servers and web interfaces',
  Rdp: 'Windows (Remote Desktop)',
  Rpc: 'Windows (managed over RPC)',
  Ssh: 'Reached over SSH (Linux, Unix, network devices)',
  Idrac: 'Dell servers with iDRAC',
  Ftp: 'File transfer (FTP)',
  Sftp: 'File transfer (SFTP)',
}

export function summarizeServers(hosts: Host[]): ServerSummary {
  const byId = new Map(hosts.map((h) => [h.id, h]))
  const runsVms = new Set(hosts.filter((h) => h.isVirtualMachine && h.hypervisorId).map((h) => h.hypervisorId))
  const tagNames = new Set(hosts.flatMap((h) => effectiveTags(h).map((t) => t.name.toLowerCase())))
  const byTag = KIND_TAGS.filter((name) => tagNames.has(name)).map((tag) => ({
    tag,
    rows: count(hosts.map((h) => tagValue(h, tag) || `No ${tag}`)),
  }))
  const byService = count(hosts.flatMap((h) => [...new Set(h.services.map((s) => SERVICE_ROLES[s.protocol]).filter((r): r is string => !!r))]))
  return {
    total: hosts.length,
    physical: hosts.filter((h) => !h.isVirtualMachine).length,
    virtual: hosts.filter((h) => h.isVirtualMachine).length,
    hypervisors: hosts.filter((h) => !h.isVirtualMachine && runsVms.has(h.id)).length,
    byOs: count(hosts.map(hostOs)),
    byTag,
    byService,
    rows: [...hosts]
      .sort((a, b) => a.name.localeCompare(b.name))
      .map((h) => ({
        name: h.name,
        address: hostAddress(h),
        kind: h.isVirtualMachine ? 'Virtual' : 'Physical',
        os: hostOs(h),
        location: hostLocation(h, byId),
        cpu: h.cpuCores ? `${h.cpuCores}` : '',
        memory: h.memoryMiB ? `${Math.round((h.memoryMiB / 1024) * 10) / 10} GB` : '',
        serial: h.serialNumber ?? '',
        services: [...new Set(h.services.map((s) => PROTOCOL_INFO[s.protocol].label))].join(', '),
      })),
  }
}

// ---- Database servers ----

const ENGINES: Partial<Record<ServiceProtocol, DbEngine>> = { Ssms: 'mssql', Oracle: 'oracle', Ase: 'ase' }

/** A database service on a host: one questionnaire "database server". */
export interface DatabaseInstance {
  key: string
  host: Host
  service: HostService
  serviceIndex: number
  engine: DbEngine
  /** e.g. "SQL Server on sql01 (SQLEXPRESS)". */
  title: string
  product: string
  location: string
}

/** The key answers are saved under: the host and its service. */
export function instanceKey(host: Host, service: HostService, index: number): string {
  return `${host.id}:${service.id ?? index}`
}

export function databaseInstances(hosts: Host[], allHosts: Host[] = hosts): DatabaseInstance[] {
  const byId = new Map(allHosts.map((h) => [h.id, h]))
  return hosts
    .flatMap((host) =>
      host.services.flatMap((service, index) => {
        const engine = ENGINES[service.protocol]
        if (!engine) {
          return []
        }
        const name = service.instance ? `${host.name} (${service.instance})` : host.name
        const port = service.port ?? PROTOCOL_INFO[service.protocol].defaultPort
        const where = hostLocation(host, byId)
        return [
          {
            key: instanceKey(host, service, index),
            host,
            service,
            serviceIndex: index,
            engine,
            title: `${PROTOCOL_INFO[service.protocol].name} on ${name}${service.label ? ` – ${service.label}` : ''}`,
            product: PROTOCOL_INFO[service.protocol].name,
            location: [`server ${host.name}${hostAddress(host) ? ` (${hostAddress(host)}${port ? `, port ${port}` : ''})` : ''}`, where].filter(Boolean).join('; '),
          },
        ]
      }),
    )
    .sort((a, b) => a.title.localeCompare(b.title))
}

/** The survey's facts about a topic: the server's own, and (when a database is named) that database's. */
export function factsFor(survey: SavedSurvey | undefined, topics: SurveyTopic[], database?: string): SurveyFact[] {
  if (!survey) {
    return []
  }
  return survey.facts.filter(
    (f) => topics.includes(f.topic) && (database === undefined ? true : f.database == null || f.database.toLowerCase() === database.toLowerCase()),
  )
}

/** Only the server-wide facts (for a server's own answers, where each database's are shown under it). */
export function serverFacts(survey: SavedSurvey | undefined, topics: SurveyTopic[]): SurveyFact[] {
  return factsFor(survey, topics).filter((f) => f.database == null)
}

/** The business's databases on a surveyed server (not the server's own). */
export function businessDatabases(survey: SavedSurvey | undefined) {
  return (survey?.databases ?? []).filter((d) => !d.system)
}

/** The answer that applies: the most specific one given. */
export function resolve<K extends string>(key: K, ...levels: (Answers<K> | undefined)[]): string {
  for (const level of levels) {
    const value = level?.[key]?.trim()
    if (value) {
      return value
    }
  }
  return ''
}

/** A new extra repository's id. */
export function newRepositoryId(): string {
  return `r${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`
}

/** Found facts as answer text: one per line, each database's prefixed with its name. */
export function factsText(found: SurveyFact[]): string {
  return found.map((f) => (f.database ? `${f.database}: ${f.text}` : f.text)).join('\n')
}

/**
 * Copies what NKeeper found into every empty answer of the servers in the report (their own, each database's
 * and their backups'), as each question's "Copy into the answer" does. Typed answers are never replaced.
 * Returns the new answers and how many were filled in.
 */
export function copyAllFound(answers: QuestionnaireAnswers, instances: DatabaseInstance[]): { answers: QuestionnaireAnswers; copied: number } {
  let copied = 0
  const fill = <K extends string>(own: Answers<K> | undefined, questions: Question<K>[], facts: (q: Question<K>) => SurveyFact[]): Answers<K> => {
    const next: Answers<K> = { ...own }
    questions.forEach((q) => {
      const found = facts(q)
      if (!next[q.key]?.trim() && found.length > 0) {
        next[q.key] = factsText(found)
        copied++
      }
    })
    return next
  }
  const result: Record<string, InstanceAnswers> = { ...answers.instances }
  instances.forEach((instance) => {
    const saved = result[instance.key]
    const survey = saved?.survey
    if (!survey || saved.excluded) {
      return
    }
    const databases: Record<string, Answers<DatabaseQuestionKey>> = { ...saved.databases }
    businessDatabases(survey).forEach((d) => {
      databases[d.name] = fill(databases[d.name], DATABASE_QUESTIONS, (q) => factsFor(survey, q.topics, d.name).filter((f) => f.database != null))
    })
    result[instance.key] = {
      ...saved,
      server: fill(saved.server, DATABASE_QUESTIONS, (q) => serverFacts(survey, q.topics)),
      databases,
      backup: fill(saved.backup, BACKUP_QUESTIONS, (q) => factsFor(survey, q.topics)),
    }
  })
  return { answers: copied > 0 ? { ...answers, instances: result } : answers, copied }
}
