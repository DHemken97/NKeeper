/**
 * The NKeeper plugin SDK for the web app: what a plugin's browser part imports as
 * `@nkeeper/plugin-sdk`. A plugin's build leaves it (and React and React Router) out of its bundle:
 * at run time they're the web app's own, so plugins share its React, its signed-in session and
 * its look. See docs/plugins.md.
 */

export { definePlugin } from './registry'
export type {
  HostFieldProps,
  HostMenuContext,
  HostSectionProps,
  Login,
  NewSession,
  NKeeperPlugin,
  PluginReport,
  ServiceMenu,
  SessionViewProps,
} from './types'
export { styles } from './styles'

// Talking to the server: the plugin's API is under /api/plugins/{id}/.
export { api, ApiError, apiUrl, apiWebSocketUrl, type ProblemDetails, type RequestOptions } from '../lib/api'
export { errorFor, toFormErrors, type FormErrors } from '../lib/formErrors'
export { useResource, type Resource } from '../features/inventory/hooks/useResource'

// Who's signed in.
export { useAuth } from '../features/auth/context'
export { isAdministrator, type User } from '../features/auth/types'

// NKeeper's controls.
export { Alert } from '../components/ui/Alert'
export { Badge } from '../components/ui/Badge'
export { Button } from '../components/ui/Button'
export { Checkbox } from '../components/ui/Checkbox'
export type { MenuEntry } from '../components/ui/ContextMenu'
export { Dialog } from '../components/ui/Dialog'
export { PasswordField } from '../components/ui/PasswordField'
export { Spinner } from '../components/ui/Spinner'
export { TextArea } from '../components/ui/TextArea'
export { TextField } from '../components/ui/TextField'

// Hosts.
export { hostsApi } from '../features/inventory/api/inventoryApi'
export { toHostDraft, toHostInput } from '../features/inventory/drafts'
export {
  formatAccount,
  formatMemory,
  primaryAddress,
  PROTOCOL_INFO,
  type Host,
  type HostCredential,
  type HostService,
  type HostTag,
  type ServiceProtocol,
} from '../features/inventory/types'

// Sessions.
export type { HostKeyMessage, RemoteSession, SessionPhase, SessionRequest } from '../features/terminal/types'
