import { Component, type ErrorInfo, type ReactNode } from 'react'
import { Alert } from '../components/ui/Alert'
import type { NKeeperPlugin, SessionViewProps } from './types'

/** A session tab shown by the plugin whose view it names; a failing plugin view stays in its tab. */
export function PluginSessionView({ plugins, ...props }: SessionViewProps & { plugins: readonly NKeeperPlugin[] }) {
  const plugin = plugins.find((p) => p.sessionViews?.[props.session.protocol])
  const View = plugin?.sessionViews?.[props.session.protocol]
  if (!plugin || !View) {
    return null
  }
  return (
    <PluginBoundary plugin={plugin.id} hidden={!props.active}>
      <View {...props} />
    </PluginBoundary>
  )
}

interface BoundaryProps {
  plugin: string
  hidden?: boolean
  children: ReactNode
}

/** Catches a plugin component's errors, so the rest of the page keeps working. */
export class PluginBoundary extends Component<BoundaryProps, { error: Error | null }> {
  state = { error: null as Error | null }

  static getDerivedStateFromError(error: Error) {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(`The ${this.props.plugin} plugin failed.`, error, info.componentStack)
  }

  render() {
    if (this.state.error) {
      return (
        <div hidden={this.props.hidden}>
          <Alert>{`The ${this.props.plugin} plugin failed here: ${this.state.error.message}`}</Alert>
        </div>
      )
    }
    return this.props.children
  }
}
