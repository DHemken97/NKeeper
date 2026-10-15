import { useState } from 'react'
import { Link } from 'react-router'
import { Alert } from '../../../components/ui/Alert'
import { Badge } from '../../../components/ui/Badge'
import { Button } from '../../../components/ui/Button'
import { Spinner } from '../../../components/ui/Spinner'
import { toFormErrors } from '../../../lib/formErrors'
import { groupsApi, tagsApi } from '../api/inventoryApi'
import { GroupFormDialog } from '../components/GroupFormDialog'
import { useResource } from '../hooks/useResource'
import type { Group } from '../types'
import styles from './InventoryPage.module.css'

export function GroupsPage() {
  const groups = useResource((signal) => groupsApi.list(signal))
  const tags = useResource((signal) => tagsApi.list(signal))
  const [editing, setEditing] = useState<Group | 'new' | null>(null)

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <div>
          <h1 className={styles.title}>Groups &amp; tags</h1>
          <p className={styles.subtitle}>A host belongs to at most one group and can carry any number of tags.</p>
        </div>
        <Button onClick={() => setEditing('new')}>Add group</Button>
      </div>

      {groups.error ? <Alert>{toFormErrors(groups.error).message}</Alert> : null}

      <section className={styles.page} aria-labelledby="groups-title">
        <h2 id="groups-title" className={styles.sectionTitle}>
          Groups
        </h2>
        <div className={styles.tableCard}>
          {groups.data === undefined && groups.loading ? (
            <div className={styles.state}>
              <Spinner label="Loading groups" />
            </div>
          ) : groups.data?.length === 0 ? (
            <div className={styles.state}>
              <p className={styles.stateTitle}>No groups yet</p>
              <p>Create groups for sites, customers or environments.</p>
            </div>
          ) : (
            <div className={styles.tableScroll}>
              <table className={styles.table}>
                <thead>
                  <tr>
                    <th scope="col">Name</th>
                    <th scope="col" className={styles.hideSmall}>
                      Description
                    </th>
                    <th scope="col" className={styles.numeric}>
                      Hosts
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {groups.data?.map((group) => (
                    <tr key={group.id}>
                      <td>
                        <button type="button" className={styles.rowLink} onClick={() => setEditing(group)}>
                          {group.name}
                        </button>
                      </td>
                      <td className={styles.hideSmall}>
                        {group.description ?? <span className={styles.muted}>—</span>}
                      </td>
                      <td className={styles.numeric}>
                        {group.hostCount > 0 ? (
                          <Link to={`/hosts?group=${group.id}`} aria-label={`Show the ${group.hostCount} hosts in ${group.name}`}>
                            {group.hostCount}
                          </Link>
                        ) : (
                          <span className={styles.muted}>0</span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </section>

      <section className={styles.page} aria-labelledby="tags-title">
        <div>
          <h2 id="tags-title" className={styles.sectionTitle}>
            Tags
          </h2>
          <p className={styles.subtitle}>Add tags from a host’s edit form. Unused tags are removed automatically.</p>
        </div>
        {tags.error ? <Alert>{toFormErrors(tags.error).message}</Alert> : null}
        {tags.data === undefined && tags.loading ? (
          <Spinner label="Loading tags" />
        ) : tags.data?.length === 0 ? (
          <p className={styles.muted}>No tags in use yet.</p>
        ) : (
          <ul className={styles.tagList}>
            {tags.data?.map((tag) => (
              <li key={tag.name}>
                <Link
                  to={`/hosts?tag=${encodeURIComponent(tag.name)}`}
                  className={styles.tagButton}
                  aria-label={`${tag.name}: ${tag.hostCount} ${tag.hostCount === 1 ? 'host' : 'hosts'}`}
                >
                  <Badge variant="tag">
                    {tag.name} <span className={styles.muted}>{tag.hostCount}</span>
                  </Badge>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </section>

      {editing ? (
        <GroupFormDialog
          group={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onChanged={() => {
            groups.reload()
            tags.reload()
          }}
        />
      ) : null}
    </div>
  )
}
