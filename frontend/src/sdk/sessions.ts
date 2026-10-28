import { serviceLabel } from '../features/inventory/hostMenu'
import { formatAccount, type Host } from '../features/inventory/types'
import type { SessionRequest } from '../features/terminal/types'
import type { Login } from './types'

/**
 * The host, service and login of a session, as the server's session requests take them
 * (SessionStartMessage): a saved credential's ID, or a one-off login. Add the plugin's own fields.
 */
export function loginBody(request: SessionRequest) {
  return {
    hostId: request.hostId,
    serviceIndex: request.serviceIndex,
    credentialId: request.credentialId,
    username: request.oneOff?.username,
    password: request.oneOff?.password,
    domain: request.oneOff?.domain,
  }
}

/** A tab's detail line: the service, what it opens, and the account, e.g. "iDRAC · Virtual console · root". */
export function sessionDetail(host: Host, serviceIndex: number, what: string | null, login: Login): string {
  const account = login.credential ? (formatAccount(login.credential) ?? login.credential.label) : login.oneOff?.username
  return [serviceLabel(host.services[serviceIndex]), what, account].filter(Boolean).join(' · ')
}
