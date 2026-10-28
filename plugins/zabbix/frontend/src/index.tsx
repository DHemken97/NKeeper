import { definePlugin, type HostFieldProps, type HostSectionProps } from '@nkeeper/plugin-sdk'
import { ZabbixHostField } from './components/ZabbixHostField'
import { ZabbixSection } from './components/ZabbixSection'
import { ZabbixSettingsSection } from './components/ZabbixSettingsPanel'
import { ZabbixReport } from './pages/ZabbixReport'
import { linkedZabbixHost } from './zabbixApi'

/** A linked host's details: what Zabbix knows about it. */
function LinkedHostSection({ host, onChanged }: HostSectionProps) {
  const zabbixHostId = linkedZabbixHost(host)
  return zabbixHostId ? <ZabbixSection key={`${host.id}:${zabbixHostId}`} host={host} onChanged={onChanged} /> : null
}

/** The host editor's Zabbix host ID: the plugin's hostId field. */
function HostIdField({ values, onChange, errors, draft }: HostFieldProps) {
  return (
    <ZabbixHostField
      value={values.hostId ?? ''}
      onChange={(id) => onChange('hostId', id)}
      error={errors.hostId}
      suggestion={draft.name.trim() || draft.ipAddress.trim() || draft.hostname.trim()}
    />
  )
}

export default definePlugin({
  id: 'zabbix',
  hostSections: [LinkedHostSection],
  hostFields: [HostIdField],
  settingsSections: [ZabbixSettingsSection],
  reports: [
    {
      path: 'zabbix',
      label: 'Zabbix',
      description: 'Hosts in Zabbix and NKeeper: link them, or add Zabbix’s to NKeeper',
      adminOnly: true,
      component: ZabbixReport,
    },
  ],
})
