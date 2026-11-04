import { useEffect, useRef, useState, type KeyboardEvent, type MouseEvent } from 'react'
import { ContextMenu, type MenuEntry } from '../../../components/ui/ContextMenu'
import { Spinner } from '../../../components/ui/Spinner'
import { ApiError } from '../../../lib/api'
import { useDbApi, useDbEngine, useDbEngineInfo } from '../engineContext'
import { scriptsFor, type SqlObjectNode, type SqlScriptAction, type SqlServerInfo } from '../sqlServerApi'
import styles from './SqlServer.module.css'

interface ObjectExplorerProps {
  sessionId: string
  server: SqlServerInfo
  onNewQuery: (database?: string) => void
  onScript: (node: SqlObjectNode, action: SqlScriptAction) => void
  /** Opens a tool from Management (the blocking manager). */
  onOpenTool: (name: string) => void
}

/** A node where it is in the tree: its path is its identity (names repeat across databases and tables). */
interface Placed {
  key: string
  node: SqlObjectNode
  depth: number
}

const ROOT_KEY = 'server'

function childKey(parent: string, node: SqlObjectNode): string {
  return `${parent}/${node.kind}:${node.objectId ?? node.name ?? node.label}`
}

/** A name to paste into a query: [schema].[name], or [name]. */
function quotedName(node: SqlObjectNode): string {
  const quote = (name: string) => `[${name.replace(/]/g, ']]')}]`
  return node.schema && node.name ? `${quote(node.schema)}.${quote(node.name)}` : quote(node.name ?? node.label)
}

/**
 * The object explorer, as in SSMS: the server, its databases (system ones in their own folder), each
 * database's tables, views, programmability and security, down to columns, keys and indexes; the
 * server's logins; and Management. Children load when a node is first expanded. Right-click (or
 * Shift+F10) for scripts: Select Top 1000 Rows, Script as CREATE or EXECUTE, and New Query.
 */
export function ObjectExplorer({ sessionId, server, onNewQuery, onScript, onOpenTool }: ObjectExplorerProps) {
  const db = useDbApi()
  const engine = useDbEngine()
  const { product } = useDbEngineInfo()
  const root: SqlObjectNode = {
    kind: 'server',
    label: `${server.serverName} (${product} ${server.productVersion} - ${server.login})`,
    expandable: true,
  }
  const [children, setChildren] = useState<Record<string, SqlObjectNode[]>>({})
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set([ROOT_KEY]))
  const [loading, setLoading] = useState<Set<string>>(new Set())
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [focused, setFocused] = useState(ROOT_KEY)
  const [menu, setMenu] = useState<{ x: number; y: number; placed: Placed } | null>(null)
  const treeRef = useRef<HTMLUListElement>(null)

  async function load(key: string, node: SqlObjectNode) {
    setLoading((current) => new Set(current).add(key))
    setErrors((current) => {
      const next = { ...current }
      delete next[key]
      return next
    })
    try {
      const list = await db.explore(sessionId, node)
      setChildren((current) => ({ ...current, [key]: list }))
    } catch (error) {
      setErrors((current) => ({ ...current, [key]: error instanceof ApiError ? error.message : 'Couldn’t load this.' }))
    } finally {
      setLoading((current) => {
        const next = new Set(current)
        next.delete(key)
        return next
      })
    }
  }

  useEffect(() => {
    void load(ROOT_KEY, { kind: 'server', label: '', expandable: true })
    // The root loads once per session.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [db, sessionId])

  function toggle(placed: Placed, open?: boolean) {
    if (!placed.node.expandable) {
      return
    }
    const isOpen = expanded.has(placed.key)
    const next = open ?? !isOpen
    if (next === isOpen) {
      return
    }
    setExpanded((current) => {
      const set = new Set(current)
      if (next) {
        set.add(placed.key)
      } else {
        set.delete(placed.key)
      }
      return set
    })
    if (next && !children[placed.key]) {
      void load(placed.key, placed.node)
    }
  }

  function refresh(placed: Placed) {
    setExpanded((current) => new Set(current).add(placed.key))
    void load(placed.key, placed.node)
  }

  /** Double-click or Enter: open a tool, else expand or collapse. */
  function activate(placed: Placed) {
    if (placed.node.kind === 'tool') {
      onOpenTool(placed.node.name ?? '')
    } else {
      toggle(placed)
    }
  }

  // The visible nodes in order, for drawing and for arrow-key movement.
  const visible: Placed[] = []
  const walk = (key: string, node: SqlObjectNode, depth: number) => {
    visible.push({ key, node, depth })
    if (expanded.has(key)) {
      for (const child of children[key] ?? []) {
        walk(childKey(key, child), child, depth + 1)
      }
    }
  }
  walk(ROOT_KEY, root, 0)

  function focusKey(key: string) {
    setFocused(key)
    requestAnimationFrame(() => treeRef.current?.querySelector<HTMLElement>(`[data-key="${CSS.escape(key)}"]`)?.focus())
  }

  function onKeyDown(event: KeyboardEvent, placed: Placed) {
    const index = visible.findIndex((v) => v.key === placed.key)
    if (event.key === 'ArrowDown' && index < visible.length - 1) {
      event.preventDefault()
      focusKey(visible[index + 1].key)
    } else if (event.key === 'ArrowUp' && index > 0) {
      event.preventDefault()
      focusKey(visible[index - 1].key)
    } else if (event.key === 'ArrowRight') {
      event.preventDefault()
      if (placed.node.expandable && !expanded.has(placed.key)) {
        toggle(placed, true)
      } else if (visible[index + 1]?.depth > placed.depth) {
        focusKey(visible[index + 1].key)
      }
    } else if (event.key === 'ArrowLeft') {
      event.preventDefault()
      if (expanded.has(placed.key)) {
        toggle(placed, false)
      } else {
        const parent = [...visible.slice(0, index)].reverse().find((v) => v.depth < placed.depth)
        if (parent) {
          focusKey(parent.key)
        }
      }
    } else if (event.key === 'Enter') {
      event.preventDefault()
      activate(placed)
    } else if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
      event.preventDefault()
      const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
      setMenu({ x: rect.left + 24, y: rect.bottom, placed })
    } else if (event.key === 'F5') {
      event.preventDefault()
      if (placed.node.expandable) {
        refresh(placed)
      }
    }
  }

  function openMenu(event: MouseEvent, placed: Placed) {
    event.preventDefault()
    setFocused(placed.key)
    setMenu({ x: event.clientX, y: event.clientY, placed })
  }

  function menuEntries({ key, node }: Placed): MenuEntry[] {
    const scripts = scriptsFor(node.kind, engine).map((s): MenuEntry => ({ kind: 'item', label: s.label, onSelect: () => onScript(node, s.action) }))
    const entries: MenuEntry[] = [{ kind: 'header', label: node.label }]
    if (node.kind === 'tool') {
      entries.push({ kind: 'item', label: 'Open', onSelect: () => onOpenTool(node.name ?? '') })
    }
    if (node.kind === 'server' || node.kind === 'database') {
      entries.push({ kind: 'item', label: 'New Query', onSelect: () => onNewQuery(node.kind === 'database' ? (node.name ?? undefined) : undefined) })
    }
    entries.push(...scripts)
    if (node.name && node.kind !== 'tool' && node.kind !== 'toolFolder') {
      entries.push({ kind: 'item', label: 'Copy name', detail: quotedName(node), onSelect: () => void navigator.clipboard?.writeText(quotedName(node)).catch(() => {}) })
    }
    if (node.expandable) {
      entries.push({ kind: 'separator' }, { kind: 'item', label: 'Refresh', shortcut: 'F5', onSelect: () => refresh({ key, node, depth: 0 }) })
    }
    return entries
  }

  return (
    <>
      <ul className={styles.tree} role="tree" aria-label="Objects" ref={treeRef}>
        {visible.map((placed) => {
          const { key, node, depth } = placed
          const isOpen = expanded.has(key)
          return (
            <li
              key={key}
              role="treeitem"
              aria-expanded={node.expandable ? isOpen : undefined}
              aria-level={depth + 1}
              aria-selected={focused === key}
              data-key={key}
              tabIndex={focused === key ? 0 : -1}
              className={`${styles.treeItem} ${focused === key ? styles.treeItemFocused : ''}`}
              style={{ paddingLeft: `${depth * 14 + 4}px` }}
              onClick={() => setFocused(key)}
              onDoubleClick={() => activate(placed)}
              onKeyDown={(event) => onKeyDown(event, placed)}
              onContextMenu={(event) => openMenu(event, placed)}
            >
              <span
                className={styles.caret}
                onClick={(event) => {
                  event.stopPropagation()
                  setFocused(key)
                  toggle(placed)
                }}
                aria-hidden="true"
              >
                {node.expandable ? (isOpen ? '▾' : '▸') : ''}
              </span>
              <span className={`${styles.treeLabel} ${node.kind.endsWith('s') || ['programmability', 'management', 'databaseSecurity', 'serverSecurity'].includes(node.kind) ? styles.folder : ''}`}>
                {node.label}
              </span>
              {node.detail ? <span className={styles.treeDetail}> ({node.detail})</span> : null}
              {loading.has(key) ? <Spinner size="sm" /> : null}
              {errors[key] ? (
                <span className={styles.treeError} role="alert">
                  {errors[key]}
                </span>
              ) : null}
              {isOpen && children[key]?.length === 0 && !loading.has(key) ? <span className={styles.treeDetail}> (empty)</span> : null}
            </li>
          )
        })}
      </ul>
      {menu ? <ContextMenu label={`Actions for ${menu.placed.node.label}`} entries={menuEntries(menu.placed)} x={menu.x} y={menu.y} onClose={() => setMenu(null)} /> : null}
    </>
  )
}
