/**
 * Test helpers for plugins' browser parts (`@nkeeper/plugin-sdk/testing`), in NKeeper's own test
 * setup: a fake server for fetch, and its JSON responses.
 */
export { antiforgeryRoute, jsonResponse, mockFetch } from '../test/http'
export { resetPlugins } from './registry'
// The whole web app, for tests that use a plugin the way people do (from the host menu, in tabs).
export { routes } from '../app/router'
export { AuthProvider } from '../features/auth/components/AuthProvider'
