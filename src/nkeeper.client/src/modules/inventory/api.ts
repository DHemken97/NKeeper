import { api } from '../../lib/api.ts';

/** Keep in sync with NKeeper.Modules.Inventory/InventoryContracts.cs. */

export type NodeOsFamily = 'Unknown' | 'Windows' | 'Linux' | 'MacOs' | 'NetworkDevice' | 'Other';
export type ConnectionProtocol = 'Ssh' | 'Rdp' | 'Vnc' | 'Telnet' | 'Http' | 'WinRm';

export const osFamilies: NodeOsFamily[] = ['Unknown', 'Windows', 'Linux', 'MacOs', 'NetworkDevice', 'Other'];
export const protocols: ConnectionProtocol[] = ['Ssh', 'Rdp', 'Vnc', 'Telnet', 'Http', 'WinRm'];

export interface Datacenter {
  id: string;
  name: string;
  location: string | null;
  description: string | null;
}

export interface ConnectionProfile {
  id: string;
  nodeId: string;
  name: string;
  protocol: ConnectionProtocol;
  port: number;
  username: string | null;
  settingsJson: string;
}

export interface InventoryNode {
  id: string;
  name: string;
  hostname: string;
  osFamily: NodeOsFamily;
  description: string | null;
  datacenterId: string | null;
  connectionProfiles: ConnectionProfile[];
}

export interface SaveDatacenter {
  name: string;
  location?: string | null;
  description?: string | null;
}

export interface SaveNode {
  name: string;
  hostname: string;
  osFamily: NodeOsFamily;
  description?: string | null;
  datacenterId: string | null;
}

export interface SaveProfile {
  name: string;
  protocol: ConnectionProtocol;
  port?: number | null;
  username?: string | null;
  settingsJson?: string | null;
}

const base = '/api/inventory';

export const inventoryApi = {
  datacenters: () => api<Datacenter[]>(`${base}/datacenters`),
  createDatacenter: (body: SaveDatacenter) => api<Datacenter>(`${base}/datacenters`, { method: 'POST', body }),
  deleteDatacenter: (id: string) => api<void>(`${base}/datacenters/${id}`, { method: 'DELETE' }),

  nodes: () => api<InventoryNode[]>(`${base}/nodes`),
  createNode: (body: SaveNode) => api<InventoryNode>(`${base}/nodes`, { method: 'POST', body }),
  updateNode: (id: string, body: SaveNode) => api<InventoryNode>(`${base}/nodes/${id}`, { method: 'PUT', body }),
  deleteNode: (id: string) => api<void>(`${base}/nodes/${id}`, { method: 'DELETE' }),

  createProfile: (nodeId: string, body: SaveProfile) =>
    api<ConnectionProfile>(`${base}/nodes/${nodeId}/profiles`, { method: 'POST', body }),
  deleteProfile: (id: string) => api<void>(`${base}/profiles/${id}`, { method: 'DELETE' }),
};
