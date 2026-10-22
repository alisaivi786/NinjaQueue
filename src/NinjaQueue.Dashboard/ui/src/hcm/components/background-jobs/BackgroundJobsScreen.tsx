'use client';

import { useState, type ReactNode } from 'react';

import { cn } from '../../utils';

import {
  PLATFORM_BACKGROUND_JOB_ACCESS,
  type BackgroundJobAccess,
  type BackgroundJobScope,
} from './background-jobs-api';
import { JobsTab } from './JobsTab';
import { QueuesWorkersTab } from './QueuesWorkersTab';
import { RecurringSchedulesTab } from './RecurringSchedulesTab';

type BackgroundJobsTab = 'jobs' | 'recurring' | 'workers';

/** Platform scope with no resolver: the tenant's id, or 'Platform' for a platform (System) job. */
function defaultTenantName(id: string | null) {
  return id ?? 'Platform';
}

export type BackgroundJobsScreenProps = {
  /**
   * 'tenant' (Admin portal): the caller's own tenant only - Jobs and Email deliveries, no tenant filter or column.
   * 'platform' (Console, super admins): every tenant, all four tabs, the tenant filter and the reseed / repair actions.
   */
  scope: BackgroundJobScope;
  /** The Background Jobs page; a job's detail page is `${basePath}/${jobId}`. */
  basePath: string;
  /** False until the caller may load data (signed in, hydrated, allowed). */
  enabled: boolean;
  /** Tenant scope: what the user may do (see backgroundJobAccess). Ignored in platform scope (everything allowed). */
  access?: BackgroundJobAccess;
  /** Platform scope: the tenant the screen is filtered to, or null for every tenant. Ignored in tenant scope. */
  tenantId?: string | null;
  /** Platform scope: a tenant's display name for the tenant column ('Platform' for null). */
  tenantName?: (id: string | null) => string;
  /** Platform scope: the app's own tenant picker, rendered above the tabs. */
  tenantFilterSlot?: ReactNode;
};

/**
 * The Background Jobs screen body (tab strip + tabs) shared by the Admin portal and the Console. Each app wraps it in
 * its own page header. The server is the gate either way; this only hides what the scope cannot use.
 */
export function BackgroundJobsScreen({
  scope,
  basePath,
  enabled,
  access,
  tenantId = null,
  tenantName = defaultTenantName,
  tenantFilterSlot,
}: BackgroundJobsScreenProps) {
  const isPlatform = scope === 'platform';
  const effectiveAccess: BackgroundJobAccess = isPlatform
    ? PLATFORM_BACKGROUND_JOB_ACCESS
    : (access ?? {
        canView: false,
        canManage: false,
        canBulk: false,
        canViewPayload: false,
      });
  // Tenant scope never filters by tenant: the server returns the caller's own tenant only.
  const tenant = isPlatform ? tenantId : null;

  const [tab, setTab] = useState<BackgroundJobsTab>('jobs');
  const tabs: Array<{
    key: BackgroundJobsTab;
    label: string;
    visible: boolean;
  }> = [
    { key: 'jobs', label: 'Jobs', visible: true },
    { key: 'recurring', label: 'Recurring schedules', visible: isPlatform },
    { key: 'workers', label: 'Queues & workers', visible: isPlatform },
  ];
  const activeTab =
    tabs.find((item) => item.key === tab && item.visible)?.key ?? 'jobs';
  // A different tenant is a different list: start its paging and selection afresh.
  const tenantKey = tenant ?? 'all';

  return (
    <div className="grid gap-4">
      {isPlatform ? (
        <div className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4 lg:flex-row lg:items-center lg:justify-between">
          <div className="min-w-0 flex-1">{tenantFilterSlot}</div>
        </div>
      ) : null}

      <div
        className="flex gap-1 overflow-x-auto border-b border-border"
        role="tablist"
      >
        {tabs
          .filter((item) => item.visible)
          .map((item) => (
            <button
              key={item.key}
              type="button"
              role="tab"
              aria-selected={activeTab === item.key}
              onClick={() => setTab(item.key)}
              className={cn(
                '-mb-px whitespace-nowrap border-b-2 px-4 py-2 text-sm font-medium transition-colors',
                activeTab === item.key
                  ? 'border-primary text-foreground'
                  : 'border-transparent text-muted-foreground hover:text-foreground',
              )}
            >
              {item.label}
            </button>
          ))}
      </div>

      {activeTab === 'jobs' ? (
        <JobsTab
          key={tenantKey}
          scope={scope}
          access={effectiveAccess}
          enabled={enabled}
          basePath={basePath}
          tenantId={tenant}
          tenantName={tenantName}
        />
      ) : null}
      {isPlatform && activeTab === 'recurring' ? (
        <RecurringSchedulesTab
          access={effectiveAccess}
          enabled={enabled}
          basePath={basePath}
        />
      ) : null}
      {isPlatform && activeTab === 'workers' ? (
        <QueuesWorkersTab enabled={enabled} />
      ) : null}
    </div>
  );
}