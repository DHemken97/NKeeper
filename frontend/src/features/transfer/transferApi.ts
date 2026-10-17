import { api } from '../../lib/api'

export interface TransferStatus {
  /** This instance can receive an import: no hosts, and no users but the caller. */
  canImport: boolean
  importBlockedReason: string | null
  /** Hosts and users are locked because the data was exported. */
  locked: boolean
  lockedAtUtc: string | null
  lockedBy: string | null
  version: string
}

/** What an export file holds (preview), or what an import brought in. */
export interface ImportSummary {
  exportedBy: string
  exportedAtUtc: string
  sourceVersion: string
  users: number
  administrators: number
  grants: number
  hosts: number
  credentials: number
  trustedKeys: number
  warnings: string[]
  imported: boolean
}

function importForm(file: File, passphrase: string, confirm: boolean): FormData {
  const form = new FormData()
  form.append('file', file)
  form.append('passphrase', passphrase)
  form.append('confirm', confirm ? 'true' : 'false')
  return form
}

export const transferApi = {
  status: (signal?: AbortSignal) => api.get<TransferStatus>('/api/admin/transfer/status', { signal }),
  export: (password: string, passphrase: string, lockAfterExport: boolean) =>
    api.post<Blob>('/api/admin/transfer/export', { body: { password, passphrase, lockAfterExport }, responseType: 'blob' }),
  unlock: () => api.post<void>('/api/admin/transfer/unlock'),
  preview: (file: File, passphrase: string) => api.post<ImportSummary>('/api/admin/transfer/import/preview', { body: importForm(file, passphrase, false) }),
  import: (file: File, passphrase: string) => api.post<ImportSummary>('/api/admin/transfer/import', { body: importForm(file, passphrase, true) }),
}

/** No look-alike characters (0/o, 1/l/i), so it can be read out or typed from paper. */
const ALPHABET = 'abcdefghjkmnpqrstuvwxyz23456789'

/** A random passphrase of six groups of five, e.g. "k7m2p-…": about 148 bits. */
export function generatePassphrase(): string {
  const chars: string[] = []
  const bytes = new Uint8Array(1)
  // Rejection sampling keeps every character equally likely.
  const limit = 256 - (256 % ALPHABET.length)
  while (chars.length < 30) {
    crypto.getRandomValues(bytes)
    if (bytes[0] < limit) {
      chars.push(ALPHABET[bytes[0] % ALPHABET.length])
    }
  }
  return Array.from({ length: 6 }, (_, i) => chars.slice(i * 5, i * 5 + 5).join('')).join('-')
}

export const MIN_PASSPHRASE_LENGTH = 16
