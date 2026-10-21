import type { HostKeyMessage, SessionRequest } from './types'

/** A virtual machine on a KVM host, as libvirt or Proxmox reports it. */
export interface VirtualMachine {
  /** libvirt's UUID, or the Proxmox VM ID. */
  id: string
  name: string
  /** The state text, e.g. "running", "shut off", "paused" (libvirt) or "running", "stopped" (Proxmox). */
  state: string
  /** The console type ("spice" or "vnc"), or null for none. */
  display: string | null
  /** The Proxmox node the VM is on; null for libvirt. */
  node?: string | null
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
    vm: request.vm?.id,
  }
}

/** The body of a Proxmox console request: the node's Proxmox service and login, and the VM ID. */
export function proxmoxConsoleBody(request: SessionRequest, trustFingerprint?: string) {
  return { ...kvmRequestBody(request), trustFingerprint, vm: Number(request.vm?.id) }
}

/** A certificate the user must trust before NKeeper connects (trust on first use). */
export interface CertificatePrompt {
  fingerprint: string
  subject: string
  expires: string
}

/** The TLS certificate prompt for trust on first use, for the HTTPS server at `target`. */
export function certificatePrompt(certificate: CertificatePrompt, target: string | null): HostKeyMessage {
  return {
    type: 'hostKey',
    status: 'unknown',
    kind: 'certificate',
    port: Number(new URL(target ?? 'https://x/').port || 443),
    algorithm: 'TLS certificate',
    fingerprint: certificate.fingerprint,
    subject: certificate.subject,
    expires: certificate.expires,
  }
}

/** The SSH host key prompt for trust on first use. */
export function hostKeyPrompt(hostKey: HostKeyPrompt): HostKeyMessage {
  return { type: 'hostKey', status: 'unknown', kind: 'ssh', port: 22, algorithm: hostKey.algorithm, fingerprint: hostKey.fingerprint }
}
