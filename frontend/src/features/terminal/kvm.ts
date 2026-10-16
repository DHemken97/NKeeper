import type { HostKeyMessage, SessionRequest } from './types'

/** A virtual machine on a KVM host, as libvirt reports it. */
export interface VirtualMachine {
  uuid: string
  name: string
  /** virsh's state text, e.g. "running", "shut off", "paused". */
  state: string
  /** The console type ("spice" or "vnc"), or null for none. */
  display: string | null
}

export interface HostKeyPrompt {
  algorithm: string
  fingerprint: string
}

/** The body of KVM requests: which host, service and login, plus a host key the user chose to trust. */
export function kvmRequestBody(request: SessionRequest, trustFingerprint?: string) {
  return {
    hostId: request.hostId,
    serviceIndex: request.serviceIndex,
    credentialId: request.credentialId,
    username: request.oneOff?.username,
    password: request.oneOff?.password,
    trustFingerprint,
    vm: request.vm?.uuid,
  }
}

/** The SSH host key prompt for trust on first use. */
export function hostKeyPrompt(hostKey: HostKeyPrompt): HostKeyMessage {
  return { type: 'hostKey', status: 'unknown', kind: 'ssh', port: 22, algorithm: hostKey.algorithm, fingerprint: hostKey.fingerprint }
}
