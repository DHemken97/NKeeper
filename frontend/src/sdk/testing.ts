/**
 * Test helpers for plugins' browser parts (`@nkeeper/plugin-sdk/testing`), in NKeeper's own test
 * setup: a fake server for fetch, and its JSON responses.
 */
export { antiforgeryRoute, jsonResponse, mockFetch } from '../test/http'
export { resetPlugins } from './registry'
