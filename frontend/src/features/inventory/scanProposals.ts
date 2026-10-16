import { newCredentialDraft, newServiceDraft, newTagDraft, type HostDraft } from './drafts'
import { PROTOCOL_INFO, type Host, type ScanFindings, type ScannedPort, type ServiceProtocol } from './types'

/** The tag a scan records the operating system in, e.g. `os: Ubuntu 24.04.1 LTS`. */
export const OS_TAG = 'os'

/** A change a scan suggests. Applying it edits the host's draft; nothing is saved until the user applies. */
export interface ScanProposal {
  key: string
  group: 'Host' | 'Services' | 'Logins'
  label: string
  detail: string
  /** Proposals that fill gaps start checked; ones that replace a value start unchecked. */
  checked: boolean
  apply: (draft: HostDraft) => void
}

const sameText = (a: string | null | undefined, b: string | null | undefined) => (a ?? '').toLowerCase() === (b ?? '').toLowerCase()

function storedPort(protocol: ServiceProtocol, port: number | null): number | null {
  return port === PROTOCOL_INFO[protocol].defaultPort ? null : port
}

function hasService(host: Host, protocol: ServiceProtocol, port: number | null) {
  const wanted = port ?? PROTOCOL_INFO[protocol].defaultPort ?? null
  return host.services.some((s) => s.protocol === protocol && (s.port ?? PROTOCOL_INFO[protocol].defaultPort ?? null) === wanted)
}

/**
 * Turns scan findings into proposed changes, leaving out what the host already has.
 * @param sshPort The SSH port an internal scan logged in on (a KVM host is reached there).
 */
export function scanProposals(host: Host, findings: ScanFindings, sshPort: number | null): ScanProposal[] {
  const proposals: ScanProposal[] = []
  const now = (current: string | null) => (current ? ` (now ${current})` : '')

  if (findings.hostname && !sameText(findings.hostname, host.hostname)) {
    const hostname = findings.hostname
    proposals.push({
      key: 'hostname',
      group: 'Host',
      label: 'Hostname',
      detail: hostname + now(host.hostname),
      checked: !host.hostname,
      apply: (draft) => void (draft.hostname = hostname),
    })
  }

  if (findings.ipAddress && findings.ipAddress !== host.ipAddress) {
    const ip = findings.ipAddress
    proposals.push({
      key: 'ip',
      group: 'Host',
      label: 'IP address',
      detail: ip + now(host.ipAddress),
      checked: !host.ipAddress,
      apply: (draft) => void (draft.ipAddress = ip),
    })
  }

  if (findings.macAddress && findings.macAddress !== host.macAddress) {
    const mac = findings.macAddress
    proposals.push({
      key: 'mac',
      group: 'Host',
      label: 'MAC address',
      detail: mac + now(host.macAddress),
      checked: !host.macAddress,
      apply: (draft) => void (draft.macAddress = mac),
    })
  }

  const os = findings.operatingSystem?.slice(0, 255)
  if (os && !host.tags.some((t) => sameText(t.name, OS_TAG) && t.value === os)) {
    const current = host.tags.find((t) => sameText(t.name, OS_TAG))?.value ?? null
    proposals.push({
      key: 'os',
      group: 'Host',
      label: 'Operating system',
      detail: `${OS_TAG}: ${os}${now(current)}`,
      checked: true,
      apply: (draft) => {
        draft.tags = [...draft.tags.filter((t) => !sameText(t.name, OS_TAG)), newTagDraft(OS_TAG, os)]
      },
    })
  }

  const services = findings.ports.filter((p): p is ScannedPort & { protocol: ServiceProtocol } => p.protocol !== null)
  if (findings.isKvmHost && sshPort !== null) {
    services.push({ port: sshPort, name: 'libvirt over SSH', protocol: 'Kvm' })
  }
  for (const { port, name, protocol } of services) {
    if (hasService(host, protocol, port)) {
      continue
    }
    const stored = storedPort(protocol, port)
    proposals.push({
      key: `service-${protocol}-${port}`,
      group: 'Services',
      label: PROTOCOL_INFO[protocol].label,
      detail: `port ${port}${name && name !== PROTOCOL_INFO[protocol].label ? ` · ${name}` : ''}`,
      checked: true,
      apply: (draft) => void draft.services.push(newServiceDraft(protocol, stored)),
    })
  }

  for (const user of findings.users) {
    if (host.credentials.some((c) => sameText(c.username, user.username))) {
      continue
    }
    proposals.push({
      key: `user-${user.username}`,
      group: 'Logins',
      label: user.username,
      detail: [user.fullName, `uid ${user.uid}`, 'no password: asks when used'].filter(Boolean).join(' · '),
      // root logins are often disabled over SSH; offer it, but don't assume it.
      checked: user.uid !== 0,
      apply: (draft) => void draft.credentials.push({ ...newCredentialDraft(), username: user.username }),
    })
  }

  return proposals
}
