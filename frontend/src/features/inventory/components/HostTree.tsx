import type { CSSProperties, KeyboardEvent, MouseEvent } from 'react'
import type { TreeGroup, TreeNode } from '../grouping'
import { PROTOCOL_INFO, primaryAddress, type HostHealth } from '../types'
import styles from './HostTree.module.css'

/** How a click selects: just this host, add/remove it (Ctrl/⌘), or a range (Shift). */
export type SelectMode = 'single' | 'toggle' | 'range'

interface HostTreeProps {
  nodes: TreeNode[]
  selectedHostId: string | null
  /** Hosts selected together for bulk changes (empty when only one host is selected). */
  bulkIds: ReadonlySet<string>
  /** Group ids the user collapsed. Everything else is expanded. */
  collapsed: ReadonlySet<string>
  onToggle: (groupId: string) => void
  onSelect: (hostId: string, mode: SelectMode) => void
  /** Double-click: connect with the first SSH service and credential. */
  onActivate: (hostId: string) => void
  /** Right-click, the Menu key or Shift+F10, at viewport coordinates. */
  onContextMenu: (hostId: string, x: number, y: number) => void
  /** Right-click (or the Menu key) on a group, e.g. to select its hosts. */
  onGroupContextMenu: (groupId: string, x: number, y: number) => void
  /** The host whose context menu is open, highlighted while it is. */
  menuHostId: string | null
  /** Hosts' service health, shown as a dot; hosts not checked yet have none. */
  health?: ReadonlyMap<string, HostHealth>
}

const HEALTH_LABELS: Record<HostHealth, string> = { up: 'All services up', partial: 'Some services down', down: 'Not answering' }

/**
 * The connection tree. Arrow keys move between rows; Left/Right collapse and expand groups.
 * Right-click (or the Menu key / Shift+F10) opens a host's menu; double-click connects.
 * Ctrl/⌘+click (or Ctrl+Space) adds a host to a bulk selection, Shift+click selects a range, and
 * a group's menu selects all its hosts. A host that matches several groups appears under each.
 */
export function HostTree({ nodes, ...rest }: HostTreeProps) {
  const { onToggle, onContextMenu, onGroupContextMenu, onSelect } = rest
  function handleKeyDown(event: KeyboardEvent<HTMLUListElement>) {
    const rows = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('[data-tree-row]'))
    const index = rows.indexOf(document.activeElement as HTMLButtonElement)
    if (index === -1) {
      return
    }
    const row = rows[index]
    const move = (to: number) => {
      event.preventDefault()
      rows[Math.max(0, Math.min(rows.length - 1, to))]?.focus()
    }

    if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
      event.preventDefault()
      const rect = row.getBoundingClientRect()
      if (row.dataset.hostId) {
        onContextMenu(row.dataset.hostId, rect.left + 24, rect.bottom)
      } else if (row.dataset.groupId) {
        onGroupContextMenu(row.dataset.groupId, rect.left + 24, rect.bottom)
      }
      return
    }

    if (row.dataset.hostId && event.key === ' ' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault()
      onSelect(row.dataset.hostId, 'toggle')
      return
    }

    switch (event.key) {
      case 'ArrowDown':
        move(index + 1)
        break
      case 'ArrowUp':
        move(index - 1)
        break
      case 'Home':
        move(0)
        break
      case 'End':
        move(rows.length - 1)
        break
      case 'ArrowRight':
        if (row.dataset.groupId && row.getAttribute('aria-expanded') === 'false') {
          event.preventDefault()
          onToggle(row.dataset.groupId)
        }
        break
      case 'ArrowLeft':
        if (row.dataset.groupId && row.getAttribute('aria-expanded') === 'true') {
          event.preventDefault()
          onToggle(row.dataset.groupId)
        }
        break
    }
  }

  return (
    <ul className={styles.tree} onKeyDown={handleKeyDown} aria-label="Hosts">
      <Nodes nodes={nodes} depth={0} {...rest} />
    </ul>
  )
}

interface NodesProps extends Omit<HostTreeProps, 'nodes'> {
  nodes: TreeNode[]
  depth: number
}

function Nodes({ nodes, depth, ...rest }: NodesProps) {
  const { selectedHostId, bulkIds, onSelect, onActivate, onContextMenu, menuHostId, health } = rest
  return nodes.map((node) =>
    node.type === 'group' ? (
      <GroupNode key={node.id} group={node} depth={depth} {...rest} />
    ) : (
      <li key={node.id}>
        <button
          type="button"
          data-tree-row
          data-host-id={node.host.id}
          className={`${styles.host} ${node.host.id === menuHostId ? styles.menuOpen : ''} ${bulkIds.has(node.host.id) ? styles.bulk : ''}`}
          style={{ '--depth': depth } as CSSProperties}
          aria-current={node.host.id === selectedHostId && bulkIds.size === 0 ? 'true' : undefined}
          aria-haspopup="menu"
          // Keep Shift+click from selecting the page's text.
          onMouseDown={(event) => event.shiftKey && event.preventDefault()}
          onClick={(event: MouseEvent) => onSelect(node.host.id, event.shiftKey ? 'range' : event.ctrlKey || event.metaKey ? 'toggle' : 'single')}
          onDoubleClick={() => onActivate(node.host.id)}
          onContextMenu={(event) => {
            event.preventDefault()
            onContextMenu(node.host.id, event.clientX, event.clientY)
          }}
        >
          {bulkIds.has(node.host.id) ? (
            <svg className={styles.check} viewBox="0 0 12 12" width="12" height="12" aria-hidden="true">
              <path d="M2.5 6.5 5 9l4.5-6" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          ) : null}
          {health?.get(node.host.id) ? (
            <span className={`${styles.health} ${styles[health.get(node.host.id)!]}`} title={HEALTH_LABELS[health.get(node.host.id)!]}>
              <span className="visually-hidden">{HEALTH_LABELS[health.get(node.host.id)!]}</span>
            </span>
          ) : null}
          <span className={styles.hostText}>
            <span className={styles.hostName}>{node.host.name}</span>
            {bulkIds.has(node.host.id) ? <span className="visually-hidden"> (selected)</span> : null}
            <span className={styles.hostAddress}>{primaryAddress(node.host)}</span>
          </span>
          {node.host.services.length > 0 ? (
            <span className={styles.service} title={node.host.services.map((s) => PROTOCOL_INFO[s.protocol].label).join(', ')}>
              {PROTOCOL_INFO[node.host.services[0].protocol].label}
              {node.host.services.length > 1 ? ` +${node.host.services.length - 1}` : null}
            </span>
          ) : null}
        </button>
      </li>
    ),
  )
}

function GroupNode({ group, depth, ...rest }: Omit<NodesProps, 'nodes'> & { group: TreeGroup }) {
  const expanded = !rest.collapsed.has(group.id)
  return (
    <li>
      <button
        type="button"
        data-tree-row
        data-group-id={group.id}
        className={`${styles.group} ${group.missing ? styles.missing : ''}`}
        style={{ '--depth': depth } as CSSProperties}
        aria-expanded={expanded}
        aria-haspopup="menu"
        onClick={() => rest.onToggle(group.id)}
        onContextMenu={(event) => {
          event.preventDefault()
          rest.onGroupContextMenu(group.id, event.clientX, event.clientY)
        }}
      >
        <svg className={styles.chevron} viewBox="0 0 12 12" width="12" height="12" aria-hidden="true">
          <path d="M4.5 2.5 8 6l-3.5 3.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <span className={styles.groupLabel}>
          {group.key.kind === 'tag' && !group.missing ? <span className={styles.groupKey}>{group.key.name}:</span> : null}
          {group.key.kind === 'hypervisor' && !group.missing ? <span className={styles.groupKey}>runs on:</span> : null}
          {group.label}
        </span>
        <span className={styles.count}>{group.hostCount}</span>
      </button>
      {expanded ? (
        <ul>
          <Nodes nodes={group.children} depth={depth + 1} {...rest} />
        </ul>
      ) : null}
    </li>
  )
}
