// Backed by HCM's BackgroundJobsAdminController (v1/admin/jobs) - the Ninja.Kit.Jobs admin API (plan §8.3, §15.11,
// §16.8, §17.8, §17.9). Enum values arrive as their names. Tenant scoping is enforced on the server: a job of another
// tenant answers 404 (never 403), and recurring schedules / workers are platform-wide (the Console only). Every
// mutation needs a reason, which the server writes to the HCM audit log.
//
// Two scopes share this module and its screens:
//   'tenant'  - the customer Admin portal: the signed-in user's own tenant only, gated by the permissions below.
//   'platform' - the super-admin Console: every tenant (optionally filtered to one), recurring schedules, workers,
//                and the tenant reseed / repair actions. The Console is super-admin only, so every action is allowed.
import axios from 'axios';
import { hasPermission, hcmApiClient, type AuthStateUser } from '@shared';

const BASE = '/jobs';

// --- Permissions ---------------------------------------------------------------------------------------------------

export const BACKGROUND_JOB_PERMISSIONS = {
  view: 'opsfusion-admin.foundation.background-jobs.view',
  manage: 'opsfusion-admin.foundation.background-jobs.manage',
  bulk: 'opsfusion-admin.foundation.background-jobs.bulk',
  viewPayload: 'opsfusion-admin.foundation.background-jobs.view-payload',
} as const;

/** 'tenant' = the Admin portal (own tenant only); 'platform' = the super-admin Console (every tenant). */
export type BackgroundJobScope = 'tenant' | 'platform';

export type BackgroundJobAccess = {
  canView: boolean;
  /** Retry / requeue / cancel one job, resend an email. The server also needs View. */
  canManage: boolean;
  /** Bulk retry / requeue / cancel. The server needs View + Manage + Bulk. */
  canBulk: boolean;
  canViewPayload: boolean;
};

/** The Console is super-admin only: everything is allowed (the server still checks every call). */
export const PLATFORM_BACKGROUND_JOB_ACCESS: BackgroundJobAccess = {
  canView: true,
  canManage: true,
  canBulk: true,
  canViewPayload: true,
};

/**
 * The Admin portal (tenant scope) access. Mirrors the server's policy chain: super admins pass every check; manage
 * needs view, bulk needs manage. Whatever the permissions, the tenant scope only ever sees its own tenant.
 */
export function backgroundJobAccess(
  user: AuthStateUser | null | undefined,
): BackgroundJobAccess {
  const isSuperAdmin = Boolean(user?.isSuperAdmin);
  const has = (code: string) => isSuperAdmin || hasPermission(user, code);
  const canView = has(BACKGROUND_JOB_PERMISSIONS.view);
  const canManage = canView && has(BACKGROUND_JOB_PERMISSIONS.manage);
  return {
    canView,
    canManage,
    canBulk: canManage && has(BACKGROUND_JOB_PERMISSIONS.bulk),
    canViewPayload: canView && has(BACKGROUND_JOB_PERMISSIONS.viewPayload),
  };
}

// --- Types ---------------------------------------------------------------------------------------------------------

export const JOB_STATUSES = [
  'Enqueued',
  'Scheduled',
  'Awaiting',
  'Processing',
  'Succeeded',
  'Cancelled',
  'DeadLettered',
] as const;
export type JobStatus = (typeof JOB_STATUSES)[number];

export const JOB_STATUS_LABELS: Record<JobStatus, string> = {
  Enqueued: 'Enqueued',
  Scheduled: 'Scheduled',
  Awaiting: 'Awaiting parent',
  Processing: 'Processing',
  Succeeded: 'Succeeded',
  Cancelled: 'Cancelled',
  DeadLettered: 'Dead-lettered',
};

export function jobStatusLabel(status: string) {
  return JOB_STATUS_LABELS[status as JobStatus] ?? status;
}

/** Active jobs can be cancelled (a Processing one cooperatively, if the server allows running-job cancel). */
export const CANCELLABLE_STATUSES: readonly string[] = [
  'Enqueued',
  'Scheduled',
  'Awaiting',
  'Processing',
];
/** Retry (§15.11): one more attempt now, dead-lettered jobs only. */
export const RETRYABLE_STATUSES: readonly string[] = ['DeadLettered'];
/** Requeue (§15.11): a fresh attempt budget, now or later, for dead-lettered or cancelled jobs. */
export const REQUEUEABLE_STATUSES: readonly string[] = [
  'DeadLettered',
  'Cancelled',
];

export type BackgroundJobSummary = {
  id: string;
  jobType: string;
  queue: string;
  tenantId: string | null;
  status: string;
  terminalReason: string | null;
  attempt: number;
  maxAttempts: number;
  createdAtUtc: string;
  runAtUtc: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  lastErrorCode: string | null;
  lastError: string | null;
  parentJobId: string | null;
  uniqueKey: string | null;
  cancelRequestedAtUtc: string | null;
  progressProcessed: number | null;
  progressTotal: number | null;
  correlationId: string | null;
  actorId: string | null;
  source: string | null;
};

export type BackgroundJobPage = {
  items: BackgroundJobSummary[];
  nextCursor: string | null;
};

export type BackgroundJobAttempt = {
  attemptSeq: number;
  charged: boolean;
  workerId: string;
  startedAtUtc: string;
  finishedAtUtc: string | null;
  durationMs: number | null;
  outcome: string | null;
  errorCode: string | null;
  error: string | null;
};

export type BackgroundJobEvent = {
  eventCode: string;
  occurredAtUtc: string;
  workerId: string | null;
  actorId: string | null;
  reason: string | null;
  data: string | null;
};

export type BackgroundJobSideEffect = {
  stepKey: string;
  status: string;
  issueCount: number;
  externalRef: string | null;
  startedAtUtc: string;
  completedAtUtc: string | null;
};

export type BackgroundJobDetail = {
  job: BackgroundJobSummary;
  /** Present only for DashboardVisible job types and callers with view-payload; masked by the server. */
  payload: string | null;
  attempts: BackgroundJobAttempt[];
  events: BackgroundJobEvent[];
  sideEffects: BackgroundJobSideEffect[];
};

export type BackgroundJobQueueStats = {
  queue: string;
  /** Enqueued + scheduled. */
  depth: number;
  oldestPendingAgeSeconds: number;
  enqueued: number;
  scheduled: number;
  awaiting: number;
  processing: number;
};

/** Active statuses are live counts; succeeded / cancelled / dead-lettered count jobs that finished in the window. */
export type BackgroundJobStatusCounts = {
  enqueued: number;
  scheduled: number;
  awaiting: number;
  processing: number;
  succeeded: number;
  cancelled: number;
  deadLettered: number;
};

export type BackgroundJobStats = {
  queues: BackgroundJobQueueStats[];
  /** Every dead-lettered job still retained, not only the window's. */
  deadLetteredCount: number;
  /** AllTenants (every job, platform jobs included) or Tenant (the caller's own tenant). */
  scope: 'AllTenants' | 'Tenant' | string;
  tenantId: string | null;
  totals: BackgroundJobStatusCounts;
  sinceUtc: string;
  throughputPerHour: number;
  /** 0..1 over the window; null when nothing finished. */
  failureRate: number | null;
};

export const STATS_WINDOW_OPTIONS = [
  { value: '1', label: 'Last hour' },
  { value: '6', label: 'Last 6 hours' },
  { value: '24', label: 'Last 24 hours' },
  { value: '72', label: 'Last 3 days' },
  { value: '168', label: 'Last 7 days' },
];

export type BackgroundJobTypeLookup = {
  code: string;
  queue: string;
  /** TenantOwned or System (platform jobs). */
  tenancy: string;
  sideEffects: string;
  maxAttempts: number;
  dashboardVisible: boolean;
};

export type BackgroundJobLookups = {
  jobTypes: BackgroundJobTypeLookup[];
  queues: string[];
  statuses: string[];
};

export type RecurringSchedule = {
  code: string;
  jobType: string;
  queue: string;
  cron: string;
  timeZone: string;
  misfirePolicy: string;
  allowOverlap: boolean;
  enabled: boolean;
  /** Disabled by an administrator (pausedBy is set). */
  paused: boolean;
  pausedBy: string | null;
  nextRunAtUtc: string;
  lastRunAtUtc: string | null;
  /** Subject id for the generic audit history (subject type RecurringSchedule). */
  auditSubjectId: string;
};

export type RecurringScheduleState = 'active' | 'paused' | 'disabled';

/** Paused = an administrator paused it; disabled without pausedBy = removed from code. */
export function recurringScheduleState(
  schedule: RecurringSchedule,
): RecurringScheduleState {
  if (schedule.enabled) return 'active';
  return schedule.paused || schedule.pausedBy ? 'paused' : 'disabled';
}

export const RECURRING_AUDIT_SUBJECT_TYPE = 'RecurringSchedule';

export type BackgroundJobWorker = {
  workerId: string;
  host: string;
  processId: number;
  appVersion: string | null;
  queues: string;
  startedAtUtc: string;
  lastHeartbeatAtUtc: string;
};

export type BackgroundJobCancelResult = { id: string; result: string };
export type BackgroundJobRunResult = {
  id: string;
  outcome: string;
  status: string | null;
  restoredContinuations: number;
  continuationConflicts: number;
};

export type BulkOperation = 'retry' | 'requeue' | 'cancel';

export type BackgroundJobBulkFilter = {
  status?: string | null;
  jobType?: string | null;
  tenantId?: string | null;
  fromUtc?: string | null;
  toUtc?: string | null;
  queue?: string | null;
};

/** Exactly one of `jobIds` (1-500) or `filter` - never both (plan §17.9). */
export type BackgroundJobBulkRequest =
  | { jobIds: string[]; filter?: never }
  | { jobIds?: never; filter: BackgroundJobBulkFilter };

export type BackgroundJobBulkItem = {
  jobId: string;
  outcome: string;
  status: string | null;
  holderJobId: string | null;
  error: string | null;
};

export type BackgroundJobBulkReport = {
  operationId: string;
  operation: string;
  status: string;
  replayed: boolean;
  requested: number;
  applied: number;
  notEligible: number;
  conflict: number;
  notFound: number;
  failed: number;
  items: BackgroundJobBulkItem[];
  createdAtUtc: string;
  completedAtUtc: string | null;
};

export const BULK_MAX_JOB_IDS = 500;
export const REASON_MAX_LENGTH = 1000;
export const JOBS_PAGE_SIZE_OPTIONS = [25, 50, 100, 200];

/** The list's own filters. The tenant filter is separate (platform scope only, chosen outside the list). */
export type JobListFilters = {
  status: string;
  queue: string;
  jobType: string;
  /** ISO 'YYYY-MM-DD' (local day). */
  fromDate: string;
  toDate: string;
};

export const EMPTY_JOB_FILTERS: JobListFilters = {
  status: '',
  queue: '',
  jobType: '',
  fromDate: '',
  toDate: '',
};

export const EMAIL_DELIVERY_STATUSES = [
  'Pending',
  'Sending',
  'Sent',
  'Failed',
  'Unknown',
  'Suppressed',
] as const;

export type EmailDelivery = {
  id: string;
  tenantId: string | null;
  mailbox: string;
  category: string;
  status: string;
  toAddress: string;
  subject: string;
  messageId: string;
  sendCount: number;
  lastError: string | null;
  serverResponse: string | null;
  jobId: string | null;
  resendOfId: string | null;
  resendNumber: number;
  canResend: boolean;
  source: string;
  createdBy: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  completedAtUtc: string | null;
};

export type EmailDeliveryPage = {
  items: EmailDelivery[];
  totalCount: number;
  page: number;
  pageSize: number;
};

export type EmailDeliveryFilters = {
  status: string;
  fromDate: string;
  toDate: string;
};

// --- Query keys ----------------------------------------------------------------------------------------------------

export const backgroundJobKeys = {
  all: ['background-jobs'] as const,
  list: (
    filters: JobListFilters,
    tenantId: string | null,
    cursor: string | null,
    pageSize: number,
  ) =>
    [
      'background-jobs',
      'list',
      filters,
      tenantId ?? '',
      cursor,
      pageSize,
    ] as const,
  detail: (id: string) => ['background-jobs', 'detail', id] as const,
  stats: (windowHours: number, tenantId: string | null = null) =>
    ['background-jobs', 'stats', windowHours, tenantId ?? ''] as const,
  lookups: ['background-jobs', 'lookups'] as const,
  recurring: ['background-jobs', 'recurring'] as const,
  workers: ['background-jobs', 'workers'] as const,
  bulk: (operationId: string) =>
    ['background-jobs', 'bulk', operationId] as const,
  emails: (
    filters: EmailDeliveryFilters,
    tenantId: string | null,
    page: number,
    pageSize: number,
  ) =>
    [
      'background-jobs',
      'email-deliveries',
      filters,
      tenantId ?? '',
      page,
      pageSize,
    ] as const,
};

// --- Helpers -------------------------------------------------------------------------------------------------------

function unwrap<T>(response: { data: { data: T } }) {
  return response.data.data;
}

/** Errors the screens explain themselves (validation, not found, forbidden, conflict, rate limit). */
const inlineErrors = {
  suppressErrorToastForStatuses: [400, 403, 404, 409, 422, 429],
};

/** A local 'YYYY-MM-DD' as the start of that day, in UTC ISO. */
function startOfLocalDayUtc(value: string) {
  return value ? new Date(`${value}T00:00:00`).toISOString() : undefined;
}

/** A local 'YYYY-MM-DD' as the end of that day (inclusive), in UTC ISO. */
function endOfLocalDayUtc(value: string) {
  return value ? new Date(`${value}T23:59:59.999`).toISOString() : undefined;
}

function blank(value: string | null | undefined) {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
}

/** The list filters (plus the platform tenant filter, if any) as the bulk filter selector. */
export function toBulkFilter(
  filters: JobListFilters,
  tenantId: string | null = null,
): BackgroundJobBulkFilter {
  return {
    status: blank(filters.status) ?? null,
    jobType: blank(filters.jobType) ?? null,
    tenantId: blank(tenantId) ?? null,
    fromUtc: startOfLocalDayUtc(filters.fromDate) ?? null,
    toUtc: endOfLocalDayUtc(filters.toDate) ?? null,
    queue: blank(filters.queue) ?? null,
  };
}

export function newIdempotencyKey() {
  if (
    typeof crypto !== 'undefined' &&
    typeof crypto.randomUUID === 'function'
  ) {
    return crypto.randomUUID();
  }
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

// --- Endpoints -----------------------------------------------------------------------------------------------------

/** `tenantId` filters to one tenant (platform scope only; tenant callers are scoped by the server). */
export async function fetchBackgroundJobs(
  filters: JobListFilters,
  tenantId: string | null,
  cursor: string | null,
  pageSize: number,
) {
  const response = await hcmApiClient.get<{ data: BackgroundJobPage }>(BASE, {
    params: {
      status: blank(filters.status),
      queue: blank(filters.queue),
      jobType: blank(filters.jobType),
      tenantId: blank(tenantId),
      fromUtc: startOfLocalDayUtc(filters.fromDate),
      toUtc: endOfLocalDayUtc(filters.toDate),
      cursor: cursor ?? undefined,
      pageSize,
    },
    ...inlineErrors,
  });
  return unwrap(response) ?? { items: [], nextCursor: null };
}

export async function fetchBackgroundJob(id: string) {
  const response = await hcmApiClient.get<{ data: BackgroundJobDetail }>(
    `${BASE}/${id}`,
    inlineErrors,
  );
  return unwrap(response);
}

/**
 * Tenant callers always get their own tenant's figures (send no `tenantId`). Platform callers get platform-wide
 * figures, or one tenant's with `tenantId`.
 */
export async function fetchBackgroundJobStats(
  windowHours: number,
  tenantId: string | null = null,
) {
  const response = await hcmApiClient.get<{ data: BackgroundJobStats }>(
    `${BASE}/stats`,
    {
      params: { windowHours, tenantId: blank(tenantId) },
      ...inlineErrors,
    },
  );
  return unwrap(response);
}

export async function fetchBackgroundJobLookups() {
  const response = await hcmApiClient.get<{ data: BackgroundJobLookups }>(
    `${BASE}/lookups`,
    inlineErrors,
  );
  return unwrap(response) ?? { jobTypes: [], queues: [], statuses: [] };
}

export async function fetchRecurringSchedules() {
  const response = await hcmApiClient.get<{ data: RecurringSchedule[] }>(
    `${BASE}/recurring`,
    inlineErrors,
  );
  return unwrap(response) ?? [];
}

export type RecurringAction = 'pause' | 'resume';

export async function runRecurringAction(
  code: string,
  action: RecurringAction,
  reason: string,
) {
  const response = await hcmApiClient.post<{ data: RecurringSchedule }>(
    `${BASE}/recurring/${encodeURIComponent(code)}/${action}`,
    { reason },
    inlineErrors,
  );
  return unwrap(response);
}

/** "Run now": the job enqueued off-schedule, and the schedule as it stands after it. */
export type RecurringTriggerResult = {
  jobId: string;
  schedule: RecurringSchedule;
};

/**
 * Starts one run of a recurring schedule now, off its timetable (POST recurring/{code}/trigger). Same rules as pause /
 * resume: view + manage (all-tenants callers), a required reason, audited. 404704 when the schedule is gone; 409707 when it is
 * disabled or paused, or a run is already going and the schedule does not allow overlap.
 */
export async function triggerRecurringSchedule(code: string, reason: string) {
  const response = await hcmApiClient.post<{ data: RecurringTriggerResult }>(
    `${BASE}/recurring/${encodeURIComponent(code)}/trigger`,
    { reason },
    inlineErrors,
  );
  return unwrap(response);
}

export async function fetchBackgroundJobWorkers() {
  const response = await hcmApiClient.get<{ data: BackgroundJobWorker[] }>(
    `${BASE}/workers`,
    inlineErrors,
  );
  return unwrap(response) ?? [];
}

export async function cancelBackgroundJob(id: string, reason: string) {
  const response = await hcmApiClient.post<{ data: BackgroundJobCancelResult }>(
    `${BASE}/${id}/cancel`,
    { reason },
    inlineErrors,
  );
  return unwrap(response);
}

export async function retryBackgroundJob(
  id: string,
  reason: string,
  includeContinuations: boolean,
) {
  const response = await hcmApiClient.post<{ data: BackgroundJobRunResult }>(
    `${BASE}/${id}/retry`,
    { reason, includeContinuations },
    inlineErrors,
  );
  return unwrap(response);
}

export async function requeueBackgroundJob(
  id: string,
  reason: string,
  includeContinuations: boolean,
  runAtUtc: string | null,
) {
  const response = await hcmApiClient.post<{ data: BackgroundJobRunResult }>(
    `${BASE}/${id}/requeue`,
    { reason, includeContinuations, runAtUtc },
    inlineErrors,
  );
  return unwrap(response);
}

/** 200 with the report for an id list; 202 with a pending report for a filter (poll {@link fetchBulkOperation}). */
export async function submitBulkOperation(
  operation: BulkOperation,
  selector: BackgroundJobBulkRequest,
  options: {
    reason: string;
    idempotencyKey: string;
    includeContinuations: boolean;
    runAtUtc: string | null;
  },
) {
  const response = await hcmApiClient.post<{ data: BackgroundJobBulkReport }>(
    `${BASE}/bulk/${operation}`,
    {
      ...(selector.jobIds
        ? { jobIds: selector.jobIds }
        : { filter: selector.filter }),
      reason: options.reason,
      idempotencyKey: options.idempotencyKey,
      includeContinuations: options.includeContinuations,
      runAtUtc: operation === 'requeue' ? options.runAtUtc : null,
    },
    inlineErrors,
  );
  return { report: unwrap(response), accepted: response.status === 202 };
}

export async function fetchBulkOperation(operationId: string) {
  const response = await hcmApiClient.get<{ data: BackgroundJobBulkReport }>(
    `${BASE}/bulk/${operationId}`,
    inlineErrors,
  );
  return unwrap(response);
}

export function isBulkFinished(
  report: BackgroundJobBulkReport | null | undefined,
) {
  return report?.status === 'Completed' || report?.status === 'Failed';
}

export async function fetchEmailDeliveries(
  filters: EmailDeliveryFilters,
  tenantId: string | null,
  page: number,
  pageSize: number,
) {
  const response = await hcmApiClient.get<{ data: EmailDeliveryPage }>(
    `${BASE}/email-deliveries`,
    {
      params: {
        status: blank(filters.status),
        tenantId: blank(tenantId),
        fromUtc: startOfLocalDayUtc(filters.fromDate),
        toUtc: endOfLocalDayUtc(filters.toDate),
        page,
        pageSize,
      },
      ...inlineErrors,
    },
  );
  return unwrap(response) ?? { items: [], totalCount: 0, page, pageSize };
}

export async function resendEmailDelivery(id: string, reason: string) {
  const response = await hcmApiClient.post<{ data: EmailDelivery }>(
    `${BASE}/email-deliveries/${id}/resend`,
    { reason },
    inlineErrors,
  );
  return unwrap(response);
}

/** The reseed jobs staged; a null id means that part was not asked for. */
export type ReseedTenantDefaultsResult = {
  shiftsJobId: string | null;
  leavePoliciesJobId: string | null;
};

/**
 * Stages the standalone reseed jobs (default shifts and/or leave policies) for one tenant - insert-only, safe to
 * repeat (POST tenants/{tenantId}/reseed-defaults, 202). At least one of the two must be chosen. Platform scope only.
 */
export async function reseedTenantDefaults(
  tenantId: string,
  options: { reason: string; shifts: boolean; leavePolicies: boolean },
) {
  const response = await hcmApiClient.post<{
    data: ReseedTenantDefaultsResult;
  }>(
    `${BASE}/tenants/${encodeURIComponent(tenantId)}/reseed-defaults`,
    {
      reason: options.reason,
      shifts: options.shifts,
      leavePolicies: options.leavePolicies,
    },
    inlineErrors,
  );
  return unwrap(response);
}

export type RepairEmployeeAccessResult = { jobId: string };

/**
 * Stages the employee access repair for one tenant: every provisioned employee account gets the tenant's Employee
 * role if it lacks it (and only then the activation email). Safe to repeat; one active repair per tenant, whose id is
 * returned again (POST tenants/{tenantId}/repair-employee-access, 202). Platform scope only.
 */
export async function repairTenantEmployeeAccess(
  tenantId: string,
  reason: string,
) {
  const response = await hcmApiClient.post<{
    data: RepairEmployeeAccessResult;
  }>(
    `${BASE}/tenants/${encodeURIComponent(tenantId)}/repair-employee-access`,
    { reason },
    inlineErrors,
  );
  return unwrap(response);
}

// --- Errors --------------------------------------------------------------------------------------------------------

/** HCM ErrorCode values for the background jobs admin API (blocks 4007xx / 4047xx / 4097xx). */
export const BACKGROUND_JOB_ERROR_CODES = {
  reasonRequired: 400701,
  bulkIdsLimitExceeded: 400702,
  bulkSelectorInvalid: 400703,
  jobNotFound: 404701,
  emailDeliveryNotFound: 404702,
  bulkOperationNotFound: 404703,
  notEligible: 409701,
  uniqueKeyConflict: 409702,
  bulkOperationActive: 409703,
  emailNotResendable: 409705,
  attemptLimitReached: 409706,
  recurringNotFound: 404704,
  recurringNotEligible: 409707,
  tenantNotFound: 40410,
} as const;

const ERROR_CODE_MESSAGES: Record<number, string> = {
  [BACKGROUND_JOB_ERROR_CODES.tenantNotFound]:
    'This tenant was not found. It may have been removed - pick the tenant again.',
  [BACKGROUND_JOB_ERROR_CODES.recurringNotFound]:
    'This recurring schedule was not found. It may have been removed - refresh the list.',
  [BACKGROUND_JOB_ERROR_CODES.recurringNotEligible]:
    'The schedule is not in a state that allows this. Only an active schedule can be paused, and only an administrator-paused schedule can be resumed (one removed from code cannot). Refresh and try again.',
  [BACKGROUND_JOB_ERROR_CODES.reasonRequired]:
    'A reason is required. It is recorded in the audit log.',
  [BACKGROUND_JOB_ERROR_CODES.bulkIdsLimitExceeded]: `At most ${BULK_MAX_JOB_IDS} jobs can be selected at once. Use "All jobs matching the filters" for larger operations.`,
  [BACKGROUND_JOB_ERROR_CODES.bulkSelectorInvalid]:
    'A bulk action needs exactly one selection: the selected jobs, or the current filters.',
  [BACKGROUND_JOB_ERROR_CODES.jobNotFound]:
    'This job was not found, or it belongs to a tenant you cannot access.',
  [BACKGROUND_JOB_ERROR_CODES.emailDeliveryNotFound]:
    'This email delivery was not found, or it belongs to a tenant you cannot access.',
  [BACKGROUND_JOB_ERROR_CODES.bulkOperationNotFound]:
    'This bulk operation was not found.',
  [BACKGROUND_JOB_ERROR_CODES.notEligible]:
    'The job is not in a state that allows this action. It may have changed since the page loaded - refresh and try again.',
  [BACKGROUND_JOB_ERROR_CODES.uniqueKeyConflict]:
    'Another active job holds the same unique key. Wait for it to finish, or cancel it first.',
  [BACKGROUND_JOB_ERROR_CODES.bulkOperationActive]:
    'Another bulk operation is still running for this tenant. Wait for it to finish, then try again.',
  [BACKGROUND_JOB_ERROR_CODES.emailNotResendable]:
    'This email cannot be resent. Only failed or unknown deliveries can be resent.',
  [BACKGROUND_JOB_ERROR_CODES.attemptLimitReached]:
    'The job has reached the attempt ceiling. Enqueue a new job instead.',
};

type ErrorEnvelope = {
  error?:
    | {
        errorCode?: number | string;
        code?: number | string;
        message?: string;
        description?: string;
      }
    | string
    | null;
  message?: string;
  title?: string;
};

export function backgroundJobErrorCode(error: unknown): number | undefined {
  if (!axios.isAxiosError(error)) return undefined;
  const data = error.response?.data as ErrorEnvelope | undefined;
  const nested =
    typeof data?.error === 'object' && data.error ? data.error : null;
  const raw = nested?.errorCode ?? nested?.code;
  const code = typeof raw === 'string' ? Number.parseInt(raw, 10) : raw;
  return typeof code === 'number' && Number.isFinite(code) ? code : undefined;
}

/** A clear, user-facing message for any failure of the background jobs admin API. */
export function backgroundJobErrorMessage(
  error: unknown,
  fallback = 'The request failed. Please try again.',
): string {
  const code = backgroundJobErrorCode(error);
  const data = axios.isAxiosError(error)
    ? (error.response?.data as ErrorEnvelope | undefined)
    : undefined;
  const nested =
    typeof data?.error === 'object' && data?.error ? data.error : null;
  const serverText =
    nested?.description || nested?.message || data?.message || data?.title;

  if (code && ERROR_CODE_MESSAGES[code]) {
    // The conflict description names the job holding the unique key - worth keeping.
    return code === BACKGROUND_JOB_ERROR_CODES.uniqueKeyConflict &&
      nested?.description
      ? `${ERROR_CODE_MESSAGES[code]} ${nested.description}`
      : ERROR_CODE_MESSAGES[code];
  }

  const status = axios.isAxiosError(error) ? error.response?.status : undefined;
  if (status === 403) {
    return (
      serverText ??
      'You do not have permission for this background jobs action.'
    );
  }
  if (status === 429) {
    return 'Too many background job actions in a short time. Wait a moment, then try again.';
  }
  return serverText ?? fallback;
}

/** {@link backgroundJobErrorMessage} for "Run now": 409707 means something different there than for pause / resume. */
export function recurringTriggerErrorMessage(error: unknown): string {
  if (
    backgroundJobErrorCode(error) ===
    BACKGROUND_JOB_ERROR_CODES.recurringNotEligible
  ) {
    return 'This schedule cannot run now. It is paused or disabled, or a run of it is still going and the schedule does not allow overlapping runs. Refresh and try again.';
  }
  return backgroundJobErrorMessage(error);
}

export function formatJobDuration(seconds: number) {
  if (!Number.isFinite(seconds) || seconds <= 0) return '-';
  if (seconds < 60) return `${Math.round(seconds)}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${Math.round(seconds % 60)}s`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours}h ${minutes % 60}m`;
  return `${Math.floor(hours / 24)}d ${hours % 24}h`;
}