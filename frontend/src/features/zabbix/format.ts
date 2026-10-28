import type { ZabbixHostOverview, ZabbixItem } from './zabbixApi'

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

function trimNumber(value: number, digits = 1): string {
  return Number(value.toFixed(digits)).toLocaleString()
}

/** Bytes in binary units, as Zabbix shows them (B, KB, MB… meaning 1024-based). */
export function formatBytes(bytes: number): string {
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB']
  let value = bytes
  let unit = 0
  while (Math.abs(value) >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${trimNumber(value, unit === 0 ? 0 : 1)} ${units[unit]}`
}

/** Seconds as "12 days, 03:04:05", like Zabbix's "uptime" unit. */
export function formatUptime(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds))
  const days = Math.floor(whole / 86_400)
  const pad = (n: number) => String(n).padStart(2, '0')
  const clock = `${pad(Math.floor((whole % 86_400) / 3600))}:${pad(Math.floor((whole % 3600) / 60))}:${pad(whole % 60)}`
  return days > 0 ? `${days} day${days === 1 ? '' : 's'}, ${clock}` : clock
}

/**
 * An item's latest value with its units, the way Zabbix displays the common ones: B (bytes),
 * uptime, s (seconds), unixtime and %. A units string starting with "!" is shown as it is.
 */
export function formatItemValue(item: Pick<ZabbixItem, 'value' | 'units' | 'key'>): string {
  const raw = item.value ?? ''
  const units = item.units ?? ''
  const number = raw.trim() === '' ? NaN : Number(raw)
  if (item.key === 'agent.ping' || item.key === 'icmpping') {
    return number === 1 ? 'Up' : number === 0 ? 'Down' : raw
  }
  if (Number.isNaN(number)) {
    return units ? `${raw} ${units.replace(/^!/, '')}` : raw
  }
  switch (units) {
    case 'B':
      return formatBytes(number)
    case 'uptime':
      return formatUptime(number)
    case 's':
      return number < 1 ? `${trimNumber(number * 1000)} ms` : `${trimNumber(number, 2)} s`
    case 'unixtime':
      return number > 0 ? dateTimeFormat.format(new Date(number * 1000)) : '—'
    case '%':
      return `${trimNumber(number)} %`
    case '':
      return Number.isInteger(number) ? number.toLocaleString() : trimNumber(number, 2)
    default:
      return `${trimNumber(number, 2)} ${units.replace(/^!/, '')}`
  }
}

/** Labels for the Zabbix inventory fields people use most; others show their field name. */
export const INVENTORY_LABELS: Record<string, string> = {
  type: 'Type',
  type_full: 'Type (full)',
  name: 'Name',
  alias: 'Alias',
  os: 'OS',
  os_full: 'OS (full)',
  os_short: 'OS (short)',
  serialno_a: 'Serial number A',
  serialno_b: 'Serial number B',
  tag: 'Tag',
  asset_tag: 'Asset tag',
  macaddress_a: 'MAC address A',
  macaddress_b: 'MAC address B',
  hardware: 'Hardware',
  hardware_full: 'Hardware (full)',
  software: 'Software',
  software_full: 'Software (full)',
  vendor: 'Vendor',
  model: 'Model',
  chassis: 'Chassis',
  hw_arch: 'Architecture',
  location: 'Location',
  location_lat: 'Latitude',
  location_lon: 'Longitude',
  site_rack: 'Rack',
  site_address_a: 'Site address',
  site_city: 'City',
  site_country: 'Country',
  contact: 'Contact',
  notes: 'Notes',
  host_networks: 'Networks',
  host_netmask: 'Netmask',
  host_router: 'Router',
  oob_ip: 'Out-of-band IP',
  installer_name: 'Installed by',
  deployment_status: 'Deployment status',
  date_hw_purchase: 'Purchased',
  date_hw_install: 'Installed',
  date_hw_expiry: 'Maintenance expires',
  date_hw_decomm: 'Decommissioned',
  contract_number: 'Contract number',
  poc_1_name: 'Primary contact',
  poc_1_email: 'Primary contact email',
  poc_1_phone_a: 'Primary contact phone',
}

/** Values Zabbix has for hardware fields NKeeper keeps on a host: serial number, MAC address, CPUs and memory. */
export interface ZabbixHardware {
  serialNumber: string | null
  macAddress: string | null
  cpuCores: number | null
  memoryMiB: number | null
}

export function zabbixHardware(overview: ZabbixHostOverview): ZabbixHardware {
  const item = (key: string) => overview.items.find((i) => i.key === key)?.value
  const cpus = Number(item('system.cpu.num'))
  const bytes = Number(item('vm.memory.size[total]'))
  return {
    serialNumber: overview.inventory.serialno_a?.trim() || null,
    macAddress: overview.host.macAddresses[0] ?? null,
    cpuCores: Number.isInteger(cpus) && cpus > 0 ? cpus : null,
    memoryMiB: Number.isFinite(bytes) && bytes > 0 ? Math.round(bytes / 1024 / 1024) : null,
  }
}
