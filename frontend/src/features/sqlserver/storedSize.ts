/** A size remembered in this browser (localStorage may be unavailable: then it's just the default). */
export function storedNumber(key: string, fallback: number): number {
  try {
    const value = Number(window.localStorage.getItem(key))
    return Number.isFinite(value) && value > 0 ? value : fallback
  } catch {
    return fallback
  }
}

export function storeNumber(key: string, value: number) {
  try {
    window.localStorage.setItem(key, String(Math.round(value * 1000) / 1000))
  } catch {
    // Not remembered; it still applies now.
  }
}
