import { api } from '../../lib/api'

export interface AuditEvent {
  id: number
  occurredAtUtc: string
  userId: string | null
  userName: string | null
  action: string
  hostId: string | null
  hostName: string | null
  detail: string | null
  sessionId: string | null
  clientAddress: string | null
  recordingId: string | null
}

export interface AuditPage {
  events: AuditEvent[]
  /** Pass as `before` for the next (older) page; null on the last. */
  next: number | null
}

export interface Recording {
  id: string
  protocol: string
  userName: string | null
  hostName: string | null
  startedAtUtc: string
  endedAtUtc: string | null
  size: number
  truncated: boolean
}

export interface AuditFilters {
  q?: string
  category?: string
  from?: string
  to?: string
}

/** The filters as a query string (dates are whole days, in the browser's time zone). */
export function auditQuery(filters: AuditFilters, extra: Record<string, string> = {}): string {
  const params = new URLSearchParams(extra)
  if (filters.q?.trim()) {
    params.set('q', filters.q.trim())
  }
  if (filters.category) {
    params.set('category', filters.category)
  }
  if (filters.from) {
    params.set('from', new Date(`${filters.from}T00:00:00`).toISOString())
  }
  if (filters.to) {
    // "To" includes that whole day.
    const end = new Date(`${filters.to}T00:00:00`)
    end.setDate(end.getDate() + 1)
    params.set('to', end.toISOString())
  }
  return params.toString()
}

export const auditApi = {
  events: (filters: AuditFilters, before?: number, signal?: AbortSignal) =>
    api.get<AuditPage>(`/api/audit/events?${auditQuery(filters, before ? { before: String(before), limit: '100' } : { limit: '100' })}`, { signal }),
  recording: (id: string, signal?: AbortSignal) => api.get<Recording>(`/api/audit/recordings/${id}`, { signal }),
  cast: async (id: string, signal?: AbortSignal) => (await api.get<Blob>(`/api/audit/recordings/${id}/cast`, { signal, responseType: 'blob' })).text(),
  csvUrl: (filters: AuditFilters) => `${import.meta.env.VITE_API_BASE_URL ?? ''}/api/audit/events.csv?${auditQuery(filters)}`,
}

/** What each action is called in the log. */
export const ACTION_LABELS: Record<string, string> = {
  'auth.signin': 'Signed in',
  'auth.signin_failed': 'Sign-in failed',
  'auth.signout': 'Signed out',
  'session.start': 'Session started',
  'session.end': 'Session ended',
  'session.failed': 'Session failed',
  'host.create': 'Host added',
  'host.update': 'Host changed',
  'host.delete': 'Host deleted',
  'host.import': 'Hosts imported',
  'host.bulk_update': 'Hosts changed together',
  'host.link_vms': 'VM links recorded',
  'host.create_from_scan': 'Hosts added from a scan',
  'host.forget_keys': 'Trusted keys forgotten',
  'host.scan': 'Host scanned',
  'host.wake': 'Wake-on-LAN sent',
  'user.create': 'User added',
  'user.update': 'User changed',
  'user.reset_password': 'Password reset',
  'user.grants': 'Access changed',
  'user.delete': 'User deleted',
  'account.change_password': 'Own password changed',
  'idrac.power': 'Power action',
  'transfer.export': 'Data exported',
  'transfer.import': 'Data imported',
  'transfer.unlock': 'Unlocked after export',
  'sftp.download': 'File downloaded',
  'sftp.upload': 'File uploaded',
  'sftp.delete': 'File deleted',
  'sftp.mkdir': 'Folder created',
  'sftp.rename': 'File renamed',
}

export const CATEGORIES = [
  { value: '', label: 'Everything' },
  { value: 'signin', label: 'Sign-ins' },
  { value: 'session', label: 'Sessions' },
  { value: 'host', label: 'Host changes' },
  { value: 'user', label: 'User changes' },
  { value: 'files', label: 'File transfers' },
  { value: 'idrac', label: 'iDRAC power' },
  { value: 'transfer', label: 'Moves to a new server' },
]

/** An asciicast v2 recording: its size and its events. */
export interface Cast {
  width: number
  height: number
  /** [seconds, "o" | "r", data] */
  events: [number, string, string][]
}

export function parseCast(text: string): Cast {
  const [first, ...rest] = text.split('\n').filter((line) => line.trim())
  const header = JSON.parse(first) as { width: number; height: number }
  return { width: header.width, height: header.height, events: rest.map((line) => JSON.parse(line) as [number, string, string]) }
}
