import { useRef, type KeyboardEvent, type PointerEvent } from 'react'
import styles from './SqlServer.module.css'

interface SplitterProps {
  /** `columns`: a vertical bar between side-by-side panes (drag left and right); `rows`: a bar between stacked panes. */
  direction: 'columns' | 'rows'
  /** e.g. "Resize Object Explorer". */
  label: string
  /** The size it controls, for screen readers (pixels or a percentage). */
  value: number
  min: number
  max: number
  /** The pointer's position while dragging (clientX for columns, clientY for rows). */
  onMove: (position: number) => void
  /** Arrow keys: a step smaller (-1) or larger (1); Shift for a big step. */
  onStep: (direction: -1 | 1, big: boolean) => void
  /** Double-click: back to the default size. */
  onReset: () => void
}

/** A draggable bar between two panes, as in SSMS. Also works from the keyboard (arrows, Home/End via onStep). */
export function Splitter({ direction, label, value, min, max, onMove, onStep, onReset }: SplitterProps) {
  const dragging = useRef(false)

  function onPointerDown(event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0) {
      return
    }
    event.preventDefault()
    dragging.current = true
    event.currentTarget.setPointerCapture?.(event.pointerId)
  }

  function onPointerMove(event: PointerEvent<HTMLDivElement>) {
    if (dragging.current) {
      onMove(direction === 'columns' ? event.clientX : event.clientY)
    }
  }

  function onPointerUp(event: PointerEvent<HTMLDivElement>) {
    dragging.current = false
    event.currentTarget.releasePointerCapture?.(event.pointerId)
  }

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const smaller = direction === 'columns' ? 'ArrowLeft' : 'ArrowUp'
    const larger = direction === 'columns' ? 'ArrowRight' : 'ArrowDown'
    if (event.key === smaller || event.key === larger) {
      event.preventDefault()
      onStep(event.key === smaller ? -1 : 1, event.shiftKey)
    }
  }

  return (
    <div
      role="separator"
      aria-label={label}
      aria-orientation={direction === 'columns' ? 'vertical' : 'horizontal'}
      aria-valuenow={Math.round(value)}
      aria-valuemin={min}
      aria-valuemax={max}
      tabIndex={0}
      className={direction === 'columns' ? styles.splitterColumns : styles.splitterRows}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      onKeyDown={onKeyDown}
      onDoubleClick={onReset}
      title="Drag to resize · double-click to reset"
    />
  )
}
