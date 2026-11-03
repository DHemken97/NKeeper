import { useState, type KeyboardEvent, type MouseEvent } from 'react'
import { ContextMenu, type MenuEntry } from '../../../components/ui/ContextMenu'
import { displayValue, tabSeparated } from '../format'
import type { SqlOutput } from '../sqlServerApi'
import styles from './SqlServer.module.css'

/** Rows drawn at first; more on request (a result set can have 10,000). */
const PAGE = 500

function copy(text: string) {
  void navigator.clipboard?.writeText(text).catch(() => {})
}

/**
 * One result set as a grid, as in SSMS: row numbers, NULLs dimmed, numbers right-aligned. Click a
 * cell to select it; Ctrl+C or right-click copies it, its row, or everything with the headers.
 */
export function ResultGrid({ output, label }: { output: SqlOutput; label: string }) {
  const columns = output.columns ?? []
  const rows = output.rows ?? []
  const [shown, setShown] = useState(PAGE)
  const [selected, setSelected] = useState<[number, number] | null>(null)
  const [menu, setMenu] = useState<{ x: number; y: number; row: number; column: number } | null>(null)
  const header = columns.map((c) => c.name)

  function menuEntries(row: number, column: number): MenuEntry[] {
    return [
      { kind: 'item', label: 'Copy', shortcut: 'Ctrl+C', onSelect: () => copy(displayValue(rows[row][column])) },
      { kind: 'item', label: 'Copy row', onSelect: () => copy(tabSeparated([rows[row]])) },
      { kind: 'item', label: 'Copy with headers', detail: `${rows.length.toLocaleString()} rows`, onSelect: () => copy(tabSeparated(rows, header)) },
      { kind: 'item', label: 'Copy column name', onSelect: () => copy(header[column]) },
    ]
  }

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (!selected) {
      return
    }
    const [row, column] = selected
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'c') {
      event.preventDefault()
      copy(displayValue(rows[row][column]))
      return
    }
    const moves: Record<string, [number, number]> = { ArrowUp: [-1, 0], ArrowDown: [1, 0], ArrowLeft: [0, -1], ArrowRight: [0, 1] }
    const move = moves[event.key]
    if (move) {
      event.preventDefault()
      const next: [number, number] = [Math.min(Math.max(row + move[0], 0), Math.min(rows.length, shown) - 1), Math.min(Math.max(column + move[1], 0), columns.length - 1)]
      setSelected(next)
    }
  }

  function openMenu(event: MouseEvent, row: number, column: number) {
    event.preventDefault()
    setSelected([row, column])
    setMenu({ x: event.clientX, y: event.clientY, row, column })
  }

  return (
    <div className={styles.grid} tabIndex={0} onKeyDown={onKeyDown} role="region" aria-label={label}>
      <table className={styles.gridTable}>
        <thead>
          <tr>
            <th scope="col" className={styles.rowNumber}>
              <span className="visually-hidden">Row</span>
            </th>
            {columns.map((column, i) => (
              <th key={i} scope="col" title={column.type} className={column.numeric ? styles.numeric : undefined}>
                {column.name}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.slice(0, shown).map((row, r) => (
            <tr key={r}>
              <th scope="row" className={styles.rowNumber}>
                {r + 1}
              </th>
              {row.map((value, c) => (
                <td
                  key={c}
                  className={[
                    columns[c]?.numeric ? styles.numeric : '',
                    value === null ? styles.nullValue : '',
                    selected?.[0] === r && selected[1] === c ? styles.selectedCell : '',
                  ].join(' ')}
                  onClick={() => setSelected([r, c])}
                  onContextMenu={(event) => openMenu(event, r, c)}
                  title={value !== null && value.length > 60 ? value.slice(0, 2000) : undefined}
                >
                  {displayValue(value)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length > shown ? (
        <button type="button" className={styles.showMore} onClick={() => setShown((n) => n + PAGE * 4)}>
          Show more rows ({(rows.length - shown).toLocaleString()} not drawn yet)
        </button>
      ) : null}
      {output.truncated ? (
        <p className={styles.truncated}>
          Showing the first {rows.length.toLocaleString()} of {(output.rowCount ?? rows.length).toLocaleString()} rows. Narrow the query (TOP, WHERE) to see the
          rest.
        </p>
      ) : null}
      {menu ? <ContextMenu label="Result cell" entries={menuEntries(menu.row, menu.column)} x={menu.x} y={menu.y} onClose={() => setMenu(null)} /> : null}
    </div>
  )
}
