import type { SessionRequest } from './types'

/** A server as its iDRAC reports it over Redfish; any field may be missing on some models or firmware. */
export interface IdracOverview {
  manufacturer: string | null
  model: string | null
  /** Dell's service tag. */
  serviceTag: string | null
  serialNumber: string | null
  hostName: string | null
  /** "On", "Off", "PoweringOn" or "PoweringOff". */
  powerState: string | null
  /** Rolled-up health: "OK", "Warning" or "Critical". */
  health: string | null
  biosVersion: string | null
  processorCount: number | null
  processorModel: string | null
  memoryGiB: number | null
  managerModel: string | null
  firmwareVersion: string | null
  /** Redfish reset types the server accepts. */
  powerActions: string[]
  console: { available: boolean; pluginType: string | null; problem: string | null }
}

export interface IdracCertificate {
  fingerprint: string
  subject: string
  issuer: string
  expires: string
}

export interface IdracOverviewResponse {
  status: 'ready' | 'trustRequired'
  overview: IdracOverview | null
  target: string | null
  certificate: IdracCertificate | null
}

/** Power actions NKeeper offers, in menu order, with whether they need confirming and the power state they apply to. */
export const POWER_ACTIONS: { type: string; label: string; when: 'on' | 'off'; confirm?: string }[] = [
  { type: 'On', label: 'Power on', when: 'off' },
  { type: 'GracefulShutdown', label: 'Shut down', when: 'on', confirm: 'asks the operating system to shut down' },
  { type: 'GracefulRestart', label: 'Restart', when: 'on', confirm: 'asks the operating system to restart' },
  { type: 'ForceRestart', label: 'Reset', when: 'on', confirm: 'restarts the server immediately, without shutting down the operating system' },
  { type: 'ForceOff', label: 'Power off', when: 'on', confirm: 'cuts the power immediately, without shutting down the operating system' },
  { type: 'PowerCycle', label: 'Power cycle', when: 'on', confirm: 'turns the power off and on again immediately' },
]

/** The body of iDRAC requests: which host, service and login, plus a certificate the user chose to trust. */
export function idracRequestBody(request: SessionRequest, trustFingerprint?: string) {
  return {
    hostId: request.hostId,
    serviceIndex: request.serviceIndex,
    credentialId: request.credentialId,
    username: request.oneOff?.username,
    password: request.oneOff?.password,
    trustFingerprint,
  }
}
