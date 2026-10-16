import { formatAccount, PROTOCOL_INFO, type Host } from './types'

export type SearchTerm = { kind: 'text'; text: string } | { kind: 'tag'; name: string; value: string }

// name:"quoted value" | name:value | "quoted text" | word
const TOKEN = /([^\s:"]+):"([^"]*)"?|([^\s:"]+):(\S*)|"([^"]*)"?|(\S+)/g

/**
 * Parses a search box query. Every term must match (AND):
 * - `sql prod` — free text, matched against names, addresses, descriptions, tags, services and accounts
 * - `env:production` / `site:"Datacenter A"` — a tag with that name whose value contains the text
 * - `env:` — any host that has an `env` tag
 */
export function parseSearch(query: string): SearchTerm[] {
  const terms: SearchTerm[] = []
  for (const match of query.matchAll(TOKEN)) {
    const [, quotedName, quotedValue, name, value, quotedText, word] = match
    if (quotedName !== undefined) {
      terms.push({ kind: 'tag', name: quotedName.toLowerCase(), value: quotedValue.trim().toLowerCase() })
    } else if (name !== undefined) {
      terms.push({ kind: 'tag', name: name.toLowerCase(), value: value.toLowerCase() })
    } else {
      const text = (quotedText ?? word ?? '').trim().toLowerCase()
      if (text) {
        terms.push({ kind: 'text', text })
      }
    }
  }
  return terms
}

function searchableText(host: Host): string {
  return [
    host.name,
    host.hostname,
    host.ipAddress,
    host.macAddress,
    host.description,
    ...host.tags.flatMap((t) => [t.name, t.value]),
    ...host.services.flatMap((s) => [PROTOCOL_INFO[s.protocol].label, s.label, s.port?.toString(), s.address]),
    ...host.credentials.flatMap((c) => [c.label, formatAccount(c)]),
  ]
    .filter(Boolean)
    .join('\n')
    .toLowerCase()
}

export function matchesSearch(host: Host, terms: SearchTerm[]): boolean {
  if (terms.length === 0) {
    return true
  }
  const text = searchableText(host)
  return terms.every((term) =>
    term.kind === 'text'
      ? text.includes(term.text)
      : host.tags.some((tag) => tag.name.toLowerCase() === term.name && (tag.value ?? '').toLowerCase().includes(term.value)),
  )
}

/** A search term that selects one tag, quoting the value when needed. */
export function tagSearchTerm(name: string, value: string | null): string {
  const v = value ?? ''
  return /[\s"]/.test(v) ? `${name}:"${v.replace(/"/g, '')}"` : `${name}:${v}`
}
