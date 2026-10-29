/**
 * Shows a session full screen, or leaves full screen. In full screen, Chromium's Keyboard Lock
 * then hands the session keys the browser otherwise keeps: Ctrl+W, Ctrl+T, Ctrl+N, Win, Alt+Tab
 * and Esc (held Esc leaves full screen). Elsewhere the session is just full screen.
 */
export async function toggleFullscreen(area: HTMLElement | null) {
  if (document.fullscreenElement) {
    await document.exitFullscreen()
    return
  }
  if (!area) {
    return
  }
  await area.requestFullscreen()
  const keyboard = (navigator as Navigator & { keyboard?: { lock?: () => Promise<void> } }).keyboard
  await keyboard?.lock?.().catch(() => {})
}
