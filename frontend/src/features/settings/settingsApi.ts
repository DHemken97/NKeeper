import { api } from '../../lib/api'

export type SmtpSecurity = 'StartTls' | 'SslOnConnect' | 'None'

/** Mirrors the API's EmailSettingsResponse. The password itself never comes back. */
export interface EmailSettings {
  enabled: boolean
  host: string
  port: number
  security: SmtpSecurity
  userName: string | null
  hasPassword: boolean
  fromAddress: string
  fromName: string | null
  updatedAtUtc: string | null
}

export interface EmailSettingsInput {
  enabled: boolean
  host: string
  port: number
  security: SmtpSecurity
  userName: string | null
  /** A new password; null keeps the saved one. */
  password: string | null
  clearPassword: boolean
  fromAddress: string
  fromName: string | null
}

export const SMTP_SECURITY: Record<SmtpSecurity, { label: string; port: number }> = {
  StartTls: { label: 'STARTTLS (usually port 587)', port: 587 },
  SslOnConnect: { label: 'TLS from the start, SMTPS (usually port 465)', port: 465 },
  None: { label: 'None: unencrypted (local relays only)', port: 25 },
}

/** Mirrors the API's DiagnosticsStatusResponse. */
export interface DiagnosticsStatus {
  enabled: boolean
  maxMegabytes: number
  minMegabytes: number
  ceilingMegabytes: number
  sizeBytes: number
  oldestUtc: string | null
  newestUtc: string | null
  lastDownloadedUtc: string | null
  droppedUtc: string | null
  pausedForDiskSpace: boolean
  writeError: string | null
  directory: string
  /** The version of NKeeper that's running. */
  version: string
}

export const diagnosticsApi = {
  get: (signal?: AbortSignal) => api.get<DiagnosticsStatus>('/api/admin/diagnostics', { signal }),
  save: (input: { enabled: boolean; maxMegabytes: number }) => api.put<DiagnosticsStatus>('/api/admin/diagnostics', { body: input }),
  download: () => api.get<Blob>('/api/admin/diagnostics/download', { responseType: 'blob' }),
}

export const settingsApi = {
  getEmail: (signal?: AbortSignal) => api.get<EmailSettings>('/api/admin/email', { signal }),
  saveEmail: (input: EmailSettingsInput) => api.put<EmailSettings>('/api/admin/email', { body: input }),
  /** Sends a test message to the signed-in administrator with these (possibly unsaved) settings. */
  testEmail: (input: EmailSettingsInput) => api.post<{ sentTo: string }>('/api/admin/email/test', { body: input }),
}
