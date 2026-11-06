import type { HostKeyPrompt } from './kvm'

/** An installed policy module; the same name can be installed at several priorities. */
export interface SelinuxModule {
  /** 100 is the distribution's; local ones are 400. */
  priority: number
  name: string
  format: string
}

/** Something SELinux denied, or an error it logged. */
export interface SelinuxDenial {
  eventId: number
  time: string
  type: string
  /** False when SELinux only logged it, in permissive mode or for a permissive domain. */
  blocked: boolean
  permissions: string[]
  process: string | null
  pid: string | null
  target: string | null
  targetClass: string | null
  sourceContext: string | null
  targetContext: string | null
  raw: string
}

export interface SelinuxStatus {
  /** Enforcing, Permissive or Disabled; null when the host has no SELinux tools. */
  mode: string | null
  modules: SelinuxModule[]
  denials: SelinuxDenial[]
  denialsTruncated: boolean
  auditProblem: string | null
}

export interface SelinuxResponse {
  status: 'ready' | 'trustRequired'
  selinux: SelinuxStatus | null
  hostKey: HostKeyPrompt | null
  source?: string | null
  message?: string | null
}

export const DISTRIBUTION_PRIORITY = 100

export const NEW_POLICY = 'module my_policy 1.0;\n\nrequire {\n\ttype httpd_t;\n\ttype var_t;\n\tclass file { read open };\n}\n\nallow httpd_t var_t:file { read open };\n'

/** A policy's name, as the server allows it. */
export const MODULE_NAME = /^[A-Za-z][A-Za-z0-9_]{0,63}$/

/** The module a policy's source declares on its first line that isn't a comment. */
export function declaredModule(source: string): string | null {
  for (const raw of source.split('\n')) {
    const line = raw.trim()
    if (line && !line.startsWith('#')) {
      return /^module\s+([A-Za-z][A-Za-z0-9_.-]*)\s+[0-9.]+\s*;/.exec(line)?.[1] ?? null
    }
  }
  return null
}

/** A name for a policy from what it allows, e.g. nkeeper_httpd_t. */
export function suggestedName(denials: SelinuxDenial[]): string {
  const domain = denials[0]?.sourceContext?.split(':')[2]
  return `nkeeper_${(domain ?? 'local').replace(/[^A-Za-z0-9_]/g, '_')}`.slice(0, 64)
}

/** Whether a denial has the text anywhere the table or its details show. */
export function denialMatches(denial: SelinuxDenial, text: string): boolean {
  const needle = text.trim().toLowerCase()
  return !needle || denial.raw.toLowerCase().includes(needle)
}
