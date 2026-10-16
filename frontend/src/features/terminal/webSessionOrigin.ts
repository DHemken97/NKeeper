// IPv4, or IPv6 (which always contains a colon; a name like "cafe" is not an address).
const IP_ADDRESS = /^(\d{1,3}\.){3}\d{1,3}$|^\[?[0-9a-f]*:[0-9a-f:.]*\]?$/i

/**
 * The address of a web session: its own origin, so the proxied app can never touch NKeeper.
 * By default that is nkw-<id>.web.<NKeeper's host name>; browsers resolve *.localhost locally.
 */
export function sessionOrigin(sessionId: string, baseDomain: string | null, location: Pick<Location, 'protocol' | 'hostname' | 'port'>): string | null {
  if (!baseDomain && IP_ADDRESS.test(location.hostname)) {
    return null
  }
  const domain = baseDomain ?? `web.${location.hostname}`
  return `${location.protocol}//nkw-${sessionId}.${domain}${location.port ? `:${location.port}` : ''}`
}
