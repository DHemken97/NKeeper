import type { SessionRequest } from './types'

/** A classic Windows event log on a host. */
export interface EventLogInfo {
  name: string
  records: number
}

export const EVENT_LEVELS = ['Error', 'Warning', 'Information', 'Audit Success', 'Audit Failure'] as const
export type EventLevel = (typeof EVENT_LEVELS)[number]

export interface EventLogEntry {
  recordNumber: number
  timeGenerated: string | null
  level: EventLevel
  eventId: number
  source: string | null
  category: string | null
  user: string | null
  computer: string | null
  message: string
}

export interface EventLogPage {
  events: EventLogEntry[]
  /** Older events matching the query remain: ask with the last event's record number. */
  hasMore: boolean
}

/** How far back to read. */
export const TIME_RANGES = [
  { hours: 1, label: 'Last hour' },
  { hours: 24, label: 'Last 24 hours' },
  { hours: 24 * 7, label: 'Last 7 days' },
  { hours: 24 * 30, label: 'Last 30 days' },
  { hours: 0, label: 'All time' },
]

/** What to read from the host. */
export interface EventLogFilters {
  log: string
  hours: number
  levels: EventLevel[]
  source: string
  eventId: string
}

/** Which host, service and login; a typed login stays in memory for the tab's lifetime, to refresh with. */
export function eventLogLogin(request: SessionRequest) {
  return {
    hostId: request.hostId,
    serviceIndex: request.serviceIndex,
    credentialId: request.credentialId,
    username: request.oneOff?.username,
    password: request.oneOff?.password,
    domain: request.oneOff?.domain,
  }
}

/** The body of a request for events, `now` being when the time range counts back from. */
export function eventLogQuery(request: SessionRequest, filters: EventLogFilters, now: Date, beforeRecord?: number) {
  return {
    ...eventLogLogin(request),
    log: filters.log,
    levels: filters.levels,
    source: filters.source.trim() || undefined,
    eventId: filters.eventId.trim() ? Number(filters.eventId) : undefined,
    since: filters.hours > 0 ? new Date(now.getTime() - filters.hours * 3_600_000).toISOString() : undefined,
    beforeRecord,
  }
}

/** Whether a typed event ID is usable: empty, or a whole number Windows can have. */
export function validEventId(text: string): boolean {
  const trimmed = text.trim()
  return trimmed === '' || (/^\d{1,5}$/.test(trimmed) && Number(trimmed) <= 65535)
}

/** Whether an event has the text anywhere the table or its details show. */
export function eventMatches(event: EventLogEntry, text: string): boolean {
  const needle = text.trim().toLowerCase()
  return (
    !needle ||
    [event.message, event.source, event.category, event.user, event.computer, event.level, String(event.eventId)].some((value) => value?.toLowerCase().includes(needle))
  )
}

/** A message's first line that has text, for the table. */
export function firstLine(message: string): string {
  return message.split(/\r?\n/).find((line) => line.trim())?.trim() ?? ''
}
