import { describe, expect, it } from 'vitest'
import { formatBytes, formatItemValue, formatUptime } from './format'

describe('Zabbix value formatting', () => {
  it('formats bytes in binary units', () => {
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(16_532_209_664)).toBe(`${(15.4).toLocaleString()} GiB`)
  })

  it('formats uptime like Zabbix', () => {
    expect(formatUptime(3_256_722)).toBe('37 days, 16:38:42')
    expect(formatUptime(59)).toBe('00:00:59')
  })

  it('formats items by their units', () => {
    expect(formatItemValue({ key: 'system.cpu.util', value: '0.8866359999999958', units: '%' })).toBe(`${(0.9).toLocaleString()} %`)
    expect(formatItemValue({ key: 'icmppingsec', value: '0.0123', units: 's' })).toBe(`${(12.3).toLocaleString()} ms`)
    expect(formatItemValue({ key: 'system.cpu.num', value: '12', units: '' })).toBe('12')
    expect(formatItemValue({ key: 'agent.ping', value: '1', units: '' })).toBe('Up')
    expect(formatItemValue({ key: 'system.sw.os', value: 'Linux version 7.0.2', units: '' })).toBe('Linux version 7.0.2')
    expect(formatItemValue({ key: 'vfs.dev.read.await[sda]', value: '0.5', units: '!ms' })).toBe(`${(0.5).toLocaleString()} ms`)
  })
})
