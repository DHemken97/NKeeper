import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { useAuth } from '../../auth/AuthContext.ts';
import { FolderIcon, PlugIcon, ServerIcon } from '../../components/icons.tsx';
import { Button, Card, ErrorBanner, Field, Input, PageHeader, Select, Spinner } from '../../components/ui.tsx';
import { errorMessage } from '../../lib/api.ts';
import { findConnectionHandler } from '../registry.ts';
import {
  inventoryApi,
  osFamilies,
  protocols,
  type ConnectionProtocol,
  type Datacenter,
  type InventoryNode,
  type NodeOsFamily,
} from './api.ts';

type Panel = { kind: 'node'; id: string } | { kind: 'new-node' } | { kind: 'new-datacenter' } | { kind: 'none' };

export default function InventoryPage() {
  const { user } = useAuth();
  const isAdmin = user?.isAdmin ?? false;

  const [datacenters, setDatacenters] = useState<Datacenter[]>([]);
  const [nodes, setNodes] = useState<InventoryNode[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState('');
  const [panel, setPanel] = useState<Panel>({ kind: 'none' });

  const reload = useCallback(
    () =>
      Promise.all([inventoryApi.datacenters(), inventoryApi.nodes()])
        .then(([dcs, ns]) => {
          setDatacenters(dcs);
          setNodes(ns);
          setError(null);
        })
        .catch((e) => setError(errorMessage(e)))
        .finally(() => setLoading(false)),
    [],
  );

  useEffect(() => {
    reload();
  }, [reload]);

  const tree = useMemo(() => {
    const term = filter.trim().toLowerCase();
    const matches = (n: InventoryNode) =>
      !term || n.name.toLowerCase().includes(term) || n.hostname.toLowerCase().includes(term);
    const groups = datacenters.map((dc) => ({
      key: dc.id,
      label: dc.name,
      nodes: nodes.filter((n) => n.datacenterId === dc.id && matches(n)),
    }));
    groups.push({ key: 'unassigned', label: 'Unassigned', nodes: nodes.filter((n) => !n.datacenterId && matches(n)) });
    return groups.filter((g) => g.nodes.length > 0 || (!term && g.key !== 'unassigned'));
  }, [datacenters, nodes, filter]);

  const selected = panel.kind === 'node' ? nodes.find((n) => n.id === panel.id) : undefined;

  return (
    <>
      <PageHeader
        title="Inventory"
        description="Datacenters, nodes and how to connect to them."
        actions={
          isAdmin && (
            <>
              <Button variant="secondary" onClick={() => setPanel({ kind: 'new-datacenter' })}>
                New datacenter
              </Button>
              <Button onClick={() => setPanel({ kind: 'new-node' })}>New node</Button>
            </>
          )
        }
      />

      {error && <ErrorBanner message={error} />}

      <div className="grid gap-6 lg:grid-cols-[18rem_1fr]">
        <Card className="self-start">
          <Input placeholder="Filter nodes…" value={filter} onChange={(e) => setFilter(e.target.value)} className="mb-3" />
          {loading ? (
            <Spinner />
          ) : tree.length === 0 ? (
            <p className="text-sm text-slate-400">No nodes yet.</p>
          ) : (
            <ul className="space-y-3 text-sm">
              {tree.map((group) => (
                <li key={group.key}>
                  <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-slate-500">
                    <FolderIcon className="size-3.5" />
                    {group.label}
                  </div>
                  <ul className="mt-1 space-y-0.5">
                    {group.nodes.map((node) => (
                      <li key={node.id}>
                        <button
                          type="button"
                          onClick={() => setPanel({ kind: 'node', id: node.id })}
                          className={
                            'flex w-full items-center gap-2 rounded px-2 py-1 text-left transition ' +
                            (selected?.id === node.id ? 'bg-sky-900/50 text-white' : 'text-slate-300 hover:bg-slate-800')
                          }
                        >
                          <ServerIcon className="size-3.5 shrink-0 text-slate-500" />
                          <span className="truncate">{node.name}</span>
                        </button>
                      </li>
                    ))}
                  </ul>
                </li>
              ))}
            </ul>
          )}
        </Card>

        <div className="min-w-0">
          {panel.kind === 'new-datacenter' && (
            <DatacenterForm
              onSaved={async () => {
                await reload();
                setPanel({ kind: 'none' });
              }}
              onCancel={() => setPanel({ kind: 'none' })}
            />
          )}
          {panel.kind === 'new-node' && (
            <NodeForm
              datacenters={datacenters}
              onSaved={async (node) => {
                await reload();
                setPanel({ kind: 'node', id: node.id });
              }}
              onCancel={() => setPanel({ kind: 'none' })}
            />
          )}
          {selected && (
            <NodeDetails
              node={selected}
              datacenter={datacenters.find((d) => d.id === selected.datacenterId)}
              isAdmin={isAdmin}
              onChanged={reload}
              onDeleted={async () => {
                await reload();
                setPanel({ kind: 'none' });
              }}
            />
          )}
          {panel.kind === 'none' && !loading && (
            <p className="pt-2 text-sm text-slate-500">Select a node to see its connection profiles.</p>
          )}
        </div>
      </div>
    </>
  );
}

function NodeDetails({ node, datacenter, isAdmin, onChanged, onDeleted }: {
  node: InventoryNode;
  datacenter?: Datacenter;
  isAdmin: boolean;
  onChanged: () => Promise<void>;
  onDeleted: () => Promise<void>;
}) {
  const navigate = useNavigate();
  const [error, setError] = useState<string | null>(null);

  async function run(action: () => Promise<unknown>, after: () => Promise<void>) {
    setError(null);
    try {
      await action();
      await after();
    } catch (e) {
      setError(errorMessage(e));
    }
  }

  return (
    <div className="space-y-6">
      <Card
        title={node.name}
        actions={
          isAdmin && (
            <Button
              variant="danger"
              onClick={() => confirm(`Delete ${node.name}?`) && run(() => inventoryApi.deleteNode(node.id), onDeleted)}
            >
              Delete
            </Button>
          )
        }
      >
        {error && <ErrorBanner message={error} />}
        <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-2 text-sm">
          <dt className="text-slate-400">Hostname</dt>
          <dd className="font-mono">{node.hostname}</dd>
          <dt className="text-slate-400">OS</dt>
          <dd>{node.osFamily}</dd>
          <dt className="text-slate-400">Datacenter</dt>
          <dd>{datacenter?.name ?? 'Unassigned'}</dd>
          {node.description && (
            <>
              <dt className="text-slate-400">Notes</dt>
              <dd className="whitespace-pre-wrap">{node.description}</dd>
            </>
          )}
        </dl>
      </Card>

      <Card title="Connection profiles">
        {node.connectionProfiles.length === 0 ? (
          <p className="text-sm text-slate-400">No connection profiles.</p>
        ) : (
          <ul className="divide-y divide-slate-800">
            {node.connectionProfiles.map((profile) => {
              const handler = findConnectionHandler(profile.protocol);
              return (
                <li key={profile.id} className="flex flex-wrap items-center justify-between gap-3 py-2 text-sm">
                  <div>
                    <span className="font-medium text-slate-100">{profile.name}</span>
                    <span className="ml-2 font-mono text-xs text-slate-400">
                      {profile.protocol.toLowerCase()}://{profile.username ? `${profile.username}@` : ''}
                      {node.hostname}:{profile.port}
                    </span>
                  </div>
                  <div className="flex gap-2">
                    <Button
                      variant="secondary"
                      disabled={!handler}
                      title={handler ? undefined : `No installed module handles ${profile.protocol} yet`}
                      onClick={() => handler && navigate(handler.route(profile.id))}
                    >
                      <PlugIcon className="size-4" />
                      {handler?.label ?? 'Connect'}
                    </Button>
                    {isAdmin && (
                      <Button variant="ghost" onClick={() => run(() => inventoryApi.deleteProfile(profile.id), onChanged)}>
                        Remove
                      </Button>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        )}
        {isAdmin && <ProfileForm nodeId={node.id} onSaved={onChanged} />}
      </Card>
    </div>
  );
}

function DatacenterForm({ onSaved, onCancel }: { onSaved: () => Promise<void>; onCancel: () => void }) {
  const [name, setName] = useState('');
  const [location, setLocation] = useState('');
  const [error, setError] = useState<string | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    try {
      await inventoryApi.createDatacenter({ name, location });
      await onSaved();
    } catch (e) {
      setError(errorMessage(e));
    }
  }

  return (
    <Card title="New datacenter">
      {error && <ErrorBanner message={error} />}
      <form onSubmit={submit} className="grid gap-4 sm:grid-cols-2">
        <Field label="Name">
          <Input autoFocus value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} />
        </Field>
        <Field label="Location">
          <Input value={location} onChange={(e) => setLocation(e.target.value)} maxLength={200} />
        </Field>
        <div className="flex gap-2 sm:col-span-2">
          <Button type="submit">Create</Button>
          <Button type="button" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
        </div>
      </form>
    </Card>
  );
}

function NodeForm({ datacenters, onSaved, onCancel }: {
  datacenters: Datacenter[];
  onSaved: (node: InventoryNode) => Promise<void>;
  onCancel: () => void;
}) {
  const [name, setName] = useState('');
  const [hostname, setHostname] = useState('');
  const [osFamily, setOsFamily] = useState<NodeOsFamily>('Unknown');
  const [datacenterId, setDatacenterId] = useState('');
  const [description, setDescription] = useState('');
  const [error, setError] = useState<string | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    try {
      const node = await inventoryApi.createNode({
        name,
        hostname,
        osFamily,
        description,
        datacenterId: datacenterId || null,
      });
      await onSaved(node);
    } catch (e) {
      setError(errorMessage(e));
    }
  }

  return (
    <Card title="New node">
      {error && <ErrorBanner message={error} />}
      <form onSubmit={submit} className="grid gap-4 sm:grid-cols-2">
        <Field label="Name">
          <Input autoFocus value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} />
        </Field>
        <Field label="Hostname or IP">
          <Input value={hostname} onChange={(e) => setHostname(e.target.value)} required maxLength={255} />
        </Field>
        <Field label="OS family">
          <Select value={osFamily} onChange={(e) => setOsFamily(e.target.value as NodeOsFamily)}>
            {osFamilies.map((os) => (
              <option key={os}>{os}</option>
            ))}
          </Select>
        </Field>
        <Field label="Datacenter">
          <Select value={datacenterId} onChange={(e) => setDatacenterId(e.target.value)}>
            <option value="">Unassigned</option>
            {datacenters.map((dc) => (
              <option key={dc.id} value={dc.id}>
                {dc.name}
              </option>
            ))}
          </Select>
        </Field>
        <div className="sm:col-span-2">
          <Field label="Notes">
            <Input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={2000} />
          </Field>
        </div>
        <div className="flex gap-2 sm:col-span-2">
          <Button type="submit">Create</Button>
          <Button type="button" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
        </div>
      </form>
    </Card>
  );
}

function ProfileForm({ nodeId, onSaved }: { nodeId: string; onSaved: () => Promise<void> }) {
  const [protocol, setProtocol] = useState<ConnectionProtocol>('Ssh');
  const [name, setName] = useState('');
  const [port, setPort] = useState('');
  const [username, setUsername] = useState('');
  const [error, setError] = useState<string | null>(null);

  async function submit(event: FormEvent) {
    event.preventDefault();
    try {
      await inventoryApi.createProfile(nodeId, {
        name: name || protocol.toUpperCase(),
        protocol,
        port: port ? Number(port) : null,
        username,
      });
      setName('');
      setPort('');
      setUsername('');
      setError(null);
      await onSaved();
    } catch (e) {
      setError(errorMessage(e));
    }
  }

  return (
    <form onSubmit={submit} className="mt-4 border-t border-slate-800 pt-4">
      {error && <ErrorBanner message={error} />}
      <div className="grid gap-3 sm:grid-cols-[8rem_1fr_6rem_1fr_auto] sm:items-end">
        <Field label="Protocol">
          <Select value={protocol} onChange={(e) => setProtocol(e.target.value as ConnectionProtocol)}>
            {protocols.map((p) => (
              <option key={p}>{p}</option>
            ))}
          </Select>
        </Field>
        <Field label="Name">
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder={protocol.toUpperCase()} maxLength={200} />
        </Field>
        <Field label="Port">
          <Input
            type="number"
            min={1}
            max={65535}
            value={port}
            onChange={(e) => setPort(e.target.value)}
            placeholder="default"
          />
        </Field>
        <Field label="Username">
          <Input value={username} onChange={(e) => setUsername(e.target.value)} maxLength={256} />
        </Field>
        <Button type="submit">Add</Button>
      </div>
    </form>
  );
}
