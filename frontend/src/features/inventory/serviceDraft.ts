import type { ServiceProtocol } from './types'

/** Form state for one service; port is kept as text so the field can be empty while typing. */
export interface ServiceDraft {
  key: number
  protocol: ServiceProtocol
  port: string
  label: string
}

let nextKey = 1

export function newServiceDraft(protocol: ServiceProtocol = 'Ssh', port: number | null = null, label: string | null = null): ServiceDraft {
  return { key: nextKey++, protocol, port: port === null ? '' : String(port), label: label ?? '' }
}
