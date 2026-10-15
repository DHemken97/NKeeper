import type { CSSProperties, KeyboardEvent } from 'react'
import type { TreeGroup, TreeNode } from '../grouping'
import { PROTOCOL_INFO, primaryAddress } from '../types'
import styles from './HostTree.module.css'

interface HostTreeProps {
  nodes: TreeNode[]
  selectedHostId: string | null
  /** Group ids the user collapsed. Everything else is expanded. */
  collapsed: ReadonlySet<string>
  onToggle: (groupId: string) => void
  onSelect: (hostId: string) => void
}

/**
 * The connection tree. Arrow keys move between rows; Left/Right collapse and expand groups.
 * A host that matches several groups appears under each of them.
 */
export function HostTree({ nodes, selectedHostId, collapsed, onToggle, onSelect }: HostTreeProps) {
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
      <Nodes nodes={nodes} depth={0} {...{ selectedHostId, collapsed, onToggle, onSelect }} />
    </ul>
  )
}

interface NodesProps extends Omit<HostTreeProps, 'nodes'> {
  nodes: TreeNode[]
  depth: number
}

function Nodes({ nodes, depth, selectedHostId, collapsed, onToggle, onSelect }: NodesProps) {
  return nodes.map((node) =>
    node.type === 'group' ? (
      <GroupNode key={node.id} group={node} depth={depth} {...{ selectedHostId, collapsed, onToggle, onSelect }} />
    ) : (
      <li key={node.id}>
        <button
          type="button"
          data-tree-row
          className={styles.host}
          style={{ '--depth': depth } as CSSProperties}
          aria-current={node.host.id === selectedHostId ? 'true' : undefined}
          onClick={() => onSelect(node.host.id)}
        >
          <span className={styles.hostText}>
            <span className={styles.hostName}>{node.host.name}</span>
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
        onClick={() => rest.onToggle(group.id)}
      >
        <svg className={styles.chevron} viewBox="0 0 12 12" width="12" height="12" aria-hidden="true">
          <path d="M4.5 2.5 8 6l-3.5 3.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <span className={styles.groupLabel}>
          {group.key.kind === 'tag' && !group.missing ? <span className={styles.groupKey}>{group.key.name}:</span> : null}
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
