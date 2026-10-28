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

/** A plugin package the server found (GET /api/admin/plugins). */
export interface PluginInfo {
  id: string
  name: string
  version: string
  description: string | null
  author: string | null
  status: 'loaded' | 'disabled' | 'failed'
  /** Why it wasn't loaded. */
  error: string | null
  /** The key ID it's signed with; null for a plugin built into the server. */
  signer: string | null
  /** Its package file. */
  source: string | null
  dependencies: string[]
  hasBackend: boolean
  hasFrontend: boolean
}

export interface PluginsOverview {
  sdkVersion: string
  plugins: PluginInfo[]
  /** Packages refused before they could be read (unsigned, untrusted, changed). */
  rejected: { source: string; error: string }[]
  trustedKeys: string[]
}

export const settingsApi = {
  getEmail: (signal?: AbortSignal) => api.get<EmailSettings>('/api/admin/email', { signal }),
  saveEmail: (input: EmailSettingsInput) => api.put<EmailSettings>('/api/admin/email', { body: input }),
  /** Sends a test message to the signed-in administrator with these (possibly unsaved) settings. */
  testEmail: (input: EmailSettingsInput) => api.post<{ sentTo: string }>('/api/admin/email/test', { body: input }),
  getPlugins: (signal?: AbortSignal) => api.get<PluginsOverview>('/api/admin/plugins', { signal }),
}
