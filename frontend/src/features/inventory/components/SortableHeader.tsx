import type { HostSortField, SortDirection } from '../types'
import styles from './SortableHeader.module.css'

interface SortableHeaderProps {
  field: HostSortField
  label: string
  query: { sort?: HostSortField; direction?: SortDirection }
  onSort: (field: HostSortField) => void
  className?: string
}

/** Column header that sorts on click and exposes the state through aria-sort. */
export function SortableHeader({ field, label, query, onSort, className }: SortableHeaderProps) {
  const active = query.sort === field
  const direction = active ? query.direction : undefined
  return (
    <th
      scope="col"
      className={className}
      aria-sort={direction === 'asc' ? 'ascending' : direction === 'desc' ? 'descending' : undefined}
    >
      <button type="button" className={`${styles.button} ${active ? styles.active : ''}`} onClick={() => onSort(field)}>
        {label}
        <svg className={styles.icon} viewBox="0 0 10 12" width="10" height="12" aria-hidden="true">
          <path d="M5 1.5 8 5H2z" className={direction === 'asc' ? styles.on : undefined} />
          <path d="M5 10.5 2 7h6z" className={direction === 'desc' ? styles.on : undefined} />
        </svg>
      </button>
    </th>
  )
}
