// What the copied screens took from HCM's shared library: the API client, a date formatter and a permission check.
import axios from 'axios';

declare module 'axios' {
  interface AxiosRequestConfig {
    /** HCM's toast-suppression hint; the dashboard shows errors inline, so it is ignored. */
    suppressErrorToastForStatuses?: number[];
  }
}

export type AuthStateUser = { isSuperAdmin?: boolean; permissions?: string[] };

export function hasPermission(user: AuthStateUser | null | undefined, code: string) {
  return Boolean(user?.isSuperAdmin) || Boolean(user?.permissions?.includes(code));
}

export function formatLocalDateTime(value?: string | null) {
  if (!value) return '-';
  const normalized = /[Zz]|[+-]\d{2}:\d{2}$/.test(value) ? value : `${value}Z`;
  const date = new Date(normalized);
  return Number.isNaN(date.getTime()) ? '-' : date.toLocaleString();
}

let selectedTenant: string | null = null;

/** The tenant picked in the tenant switcher; sent with every API call (null: every tenant the user may see). */
export function setSelectedTenant(tenantId: string | null) {
  selectedTenant = tenantId;
}

export const AUTH_EVENT = 'ninjaqueue:auth';

/** The dashboard API, relative to the page (the dashboard can be mapped at any path). */
export const hcmApiClient = axios.create({
  baseURL: './api',
  withCredentials: true,
  headers: { 'X-NinjaQueue-Csrf': '1' },
});

hcmApiClient.interceptors.request.use((config) => {
  if (selectedTenant) {
    config.headers.set('X-NinjaQueue-Tenant', selectedTenant);
  }
  return config;
});

hcmApiClient.interceptors.response.use(
  (response) => response,
  (error) => {
    const status = error?.response?.status;
    const code = error?.response?.data?.error?.code;
    if (status === 401 || (status === 403 && code === 'PasswordChangeRequired')) {
      window.dispatchEvent(new Event(AUTH_EVENT));
    }
    return Promise.reject(error);
  },
);

/** The server's error message, for inline display. */
export function errorMessage(error: unknown, fallback = 'Something went wrong.') {
  if (axios.isAxiosError(error)) {
    const body = error.response?.data as { error?: { message?: string; hint?: string } } | undefined;
    if (body?.error?.message) {
      return body.error.hint ? `${body.error.message} ${body.error.hint}` : body.error.message;
    }
  }
  return fallback;
}