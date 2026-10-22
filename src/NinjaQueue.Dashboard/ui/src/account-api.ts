import { hcmApiClient as api } from '@shared';

export type Tenant = { tenantId: string; name: string };

export type Me = {
  id: string;
  userName: string;
  isAdmin: boolean;
  mustChangePassword: boolean;
  tenants: Tenant[];
  title: string;
};

export type DashboardUser = {
  id: string;
  userName: string;
  isAdmin: boolean;
  mustChangePassword: boolean;
  lockedOut: boolean;
  tenantIds: string[];
  createdAtUtc: string;
};

/** The system tenant: platform jobs that belong to no tenant. */
export const SYSTEM_TENANT = '00000000-0000-0000-0000-000000000000';

type Envelope<T> = { data: T };

export const accountKeys = {
  me: ['account', 'me'] as const,
  users: ['account', 'users'] as const,
  tenants: ['account', 'tenants'] as const,
};

export async function fetchMe() {
  return (await api.get<Envelope<Me>>('/auth/me')).data.data;
}

export async function login(userName: string, password: string) {
  return (await api.post<Envelope<Me>>('/auth/login', { userName, password })).data.data;
}

export async function logout() {
  await api.post('/auth/logout');
}

export async function changePassword(currentPassword: string, newPassword: string) {
  return (await api.post<Envelope<Me>>('/auth/change-password', { currentPassword, newPassword })).data.data;
}

export async function fetchUsers() {
  return (await api.get<Envelope<DashboardUser[]>>('/users')).data.data;
}

export async function createUser(user: { userName: string; password: string; isAdmin: boolean; tenantIds: string[] }) {
  return (await api.post<Envelope<DashboardUser>>('/users', user)).data.data;
}

export async function updateUser(id: string, user: { isAdmin: boolean; tenantIds: string[]; password?: string }) {
  return (await api.put<Envelope<DashboardUser>>(`/users/${id}`, user)).data.data;
}

export async function deleteUser(id: string) {
  await api.delete(`/users/${id}`);
}

export async function fetchTenants() {
  return (await api.get<Envelope<Tenant[]>>('/tenants')).data.data;
}

export async function saveTenant(tenant: { tenantId?: string | null; name: string }) {
  return (await api.post<Envelope<Tenant>>('/tenants', tenant)).data.data;
}

export async function deleteTenant(id: string) {
  await api.delete(`/tenants/${id}`);
}