import field from '../components/ui/TextField.module.css'
import hostPanel from '../features/inventory/components/HostPanel.module.css'
import listEditor from '../features/inventory/components/ListEditor.module.css'
import report from '../features/reports/pages/Reports.module.css'
import settings from '../features/settings/pages/SettingsPage.module.css'
import session from '../features/terminal/components/Session.module.css'
import page from '../features/transfer/pages/TransferPage.module.css'

/**
 * NKeeper's own CSS module classes, so a plugin's parts look like the pages around them:
 * - `hostPanel`: a host's details (section, sectionTitle, dl…);
 * - `page`: a page or Settings panel (page, panel, panelTitle, hint, form…);
 * - `settings`: Settings extras (actions, certificate, fingerprint);
 * - `report`: a report (report, toolbar, table…);
 * - `session`: a session tab (session, terminalArea…);
 * - `field`: a form field's parts (field, label, input) for controls TextField doesn't cover;
 * - `listEditor`: a list of editable rows.
 */
export const styles = { field, hostPanel, listEditor, page, report, session, settings }
