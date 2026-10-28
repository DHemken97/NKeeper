import {
  api,
  definePlugin,
  serviceLabel,
  sessionDetail,
  WebSessionView,
  type Host,
  type HostMenuContext,
  type Login,
  type MenuEntry,
  type OpenWebSessionResponse,
  type SessionViewProps,
} from '@nkeeper/plugin-sdk'
import { IDRAC_MODES, idracRequestBody, type IdracMode } from './idrac'
import { IdracView } from './IdracView'

/** Opens one of an iDRAC's modes in a tab of the host's, with a login. */
function open(host: Host, serviceIndex: number, mode: IdracMode | 'web', login: Login, menu: Pick<HostMenuContext, 'openSession'>) {
  const info = mode === 'web' ? { label: 'Web interface', view: 'web' } : IDRAC_MODES[mode]
  menu.openSession({
    hostId: host.id,
    view: info.view,
    title: host.name,
    detail: sessionDetail(host, serviceIndex, info.label, login),
    request: { hostId: host.id, serviceIndex, credentialId: login.credential?.id, oneOff: login.oneOff },
  })
}

/** Connect ▸ iDRAC ▸ Virtual console, Status & power, RACADM (each ▸ its logins), and the web interface. */
function idracEntry(host: Host, serviceIndex: number, menu: HostMenuContext): MenuEntry {
  const service = host.services[serviceIndex]
  return {
    kind: 'item',
    label: serviceLabel(service),
    detail: service.address ?? `:${service.port ?? 443}`,
    submenu: [
      ...(Object.keys(IDRAC_MODES) as IdracMode[]).map(
        (mode): MenuEntry => ({
          kind: 'item',
          label: IDRAC_MODES[mode].label,
          detail: IDRAC_MODES[mode].detail,
          submenu: menu.loginEntries(host, serviceIndex, (login) => open(host, serviceIndex, mode, login, menu)),
        }),
      ),
      { kind: 'separator' },
      { kind: 'item', label: 'Web interface', submenu: menu.webEntries(host, serviceIndex) },
    ],
  }
}

/** Status & power, whose buttons open the other modes with the same login. */
function OverviewView({ session, active, onShowHost, host, onHostChanged, openSession }: SessionViewProps) {
  return (
    <IdracView
      session={session}
      active={active}
      onShowHost={onShowHost}
      host={host}
      onHostChanged={onHostChanged}
      onOpen={(mode) => {
        if (host) {
          const credential = host.credentials.find((c) => c.id === session.request.credentialId)
          open(host, session.request.serviceIndex, mode, { credential, oneOff: session.request.oneOff }, { openSession })
        }
      }}
    />
  )
}

/** The HTML5 virtual console: a web session that lands on the console page with one-time credentials. */
function ConsoleView({ session, active, onShowHost, onEnded }: SessionViewProps) {
  return (
    <WebSessionView
      session={session}
      active={active}
      onShowHost={onShowHost}
      onEnded={onEnded}
      what="virtual console"
      open={(trustFingerprint) => api.post<OpenWebSessionResponse>('/api/plugins/idrac/console', { body: idracRequestBody(session.request, trustFingerprint) })}
    />
  )
}

export default definePlugin({
  id: 'idrac',
  serviceMenus: {
    Idrac: {
      entry: idracEntry,
      // Double-click opens the virtual console.
      open: (host, serviceIndex, login, menu) => open(host, serviceIndex, 'console', login, menu),
    },
  },
  sessionViews: { idrac: OverviewView, idracconsole: ConsoleView },
})
