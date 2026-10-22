import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { KeyRound, Plus, ShieldCheck, Trash2 } from 'lucide-react';

import { errorMessage, formatLocalDateTime } from '@shared';
import { Button } from './hcm/components/ui/button';
import { Input } from './hcm/components/ui/input';
import { Label } from './hcm/components/ui/label';
import {
  accountKeys,
  createUser,
  deleteTenant,
  deleteUser,
  fetchTenants,
  fetchUsers,
  saveTenant,
  SYSTEM_TENANT,
  updateUser,
  type DashboardUser,
  type Me,
  type Tenant,
} from './account-api';

function Section({ title, description, children }: { title: string; description: string; children: React.ReactNode }) {
  return (
    <section className="rounded-xl border border-border bg-card p-5">
      <h2 className="text-base font-semibold text-foreground">{title}</h2>
      <p className="mb-4 text-sm text-muted-foreground">{description}</p>
      {children}
    </section>
  );
}

function TenantChecklist({ tenants, value, onChange }: { tenants: Tenant[]; value: string[]; onChange: (ids: string[]) => void }) {
  if (tenants.length === 0) {
    return <p className="text-sm text-muted-foreground">Add a tenant first.</p>;
  }
  return (
    <div className="flex flex-wrap gap-2">
      {tenants.map((t) => {
        const checked = value.includes(t.tenantId);
        return (
          <label
            key={t.tenantId}
            className={`cursor-pointer rounded-full border px-3 py-1 text-sm ${checked ? 'border-primary bg-primary/10 text-foreground' : 'border-border text-muted-foreground'}`}
          >
            <input
              type="checkbox"
              className="sr-only"
              checked={checked}
              onChange={() => onChange(checked ? value.filter((id) => id !== t.tenantId) : [...value, t.tenantId])}
            />
            {t.name}
          </label>
        );
      })}
    </div>
  );
}

export function UsersPage({ me }: { me: Me }) {
  const queryClient = useQueryClient();
  const usersQuery = useQuery({ queryKey: accountKeys.users, queryFn: fetchUsers });
  const tenantsQuery = useQuery({ queryKey: accountKeys.tenants, queryFn: fetchTenants });
  const tenants = (tenantsQuery.data ?? []).filter((t) => t.tenantId !== SYSTEM_TENANT);
  const tenantName = (id: string) => tenants.find((t) => t.tenantId === id)?.name ?? id;
  const [error, setError] = useState<string | null>(null);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['account'] });
  };
  const run = useMutation({
    mutationFn: (action: () => Promise<unknown>) => action(),
    onSuccess: () => {
      setError(null);
      refresh();
    },
    onError: (e) => setError(errorMessage(e)),
  });

  // New tenant
  const [tenantNameInput, setTenantNameInput] = useState('');
  const [tenantIdInput, setTenantIdInput] = useState('');
  function addTenant(event: FormEvent) {
    event.preventDefault();
    run.mutate(async () => {
      await saveTenant({ tenantId: tenantIdInput.trim() || null, name: tenantNameInput });
      setTenantNameInput('');
      setTenantIdInput('');
    });
  }

  // New user
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [isAdmin, setIsAdmin] = useState(false);
  const [userTenants, setUserTenants] = useState<string[]>([]);
  function addUser(event: FormEvent) {
    event.preventDefault();
    run.mutate(async () => {
      await createUser({ userName, password, isAdmin, tenantIds: isAdmin ? [] : userTenants });
      setUserName('');
      setPassword('');
      setIsAdmin(false);
      setUserTenants([]);
    });
  }

  const [editing, setEditing] = useState<DashboardUser | null>(null);

  return (
    <div className="grid gap-5">
      {error ? <p className="rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-2 text-sm text-destructive">{error}</p> : null}

      <Section title="Tenants" description="Jobs carry a tenant id. Name your tenants here so people can switch between them and be assigned to them.">
        <form className="mb-4 grid gap-3 md:grid-cols-[1fr_1fr_auto] md:items-end" onSubmit={addTenant}>
          <div className="grid gap-1.5">
            <Label htmlFor="tenantName">Name</Label>
            <Input id="tenantName" value={tenantNameInput} onChange={(e) => setTenantNameInput(e.target.value)} required maxLength={200} />
          </div>
          <div className="grid gap-1.5">
            <Label htmlFor="tenantId">Tenant id (optional: the id your jobs use)</Label>
            <Input id="tenantId" value={tenantIdInput} onChange={(e) => setTenantIdInput(e.target.value)} placeholder="generated when empty" />
          </div>
          <Button type="submit" disabled={run.isPending}>
            <Plus className="h-4 w-4" /> Add tenant
          </Button>
        </form>
        <div className="divide-y divide-border rounded-lg border border-border">
          {tenants.map((t) => (
            <div key={t.tenantId} className="flex items-center justify-between gap-3 px-4 py-2 text-sm">
              <div>
                <p className="font-medium text-foreground">{t.name}</p>
                <p className="font-mono text-xs text-muted-foreground">{t.tenantId}</p>
              </div>
              <Button
                type="button"
                size="icon"
                variant="ghost"
                aria-label={`Delete ${t.name}`}
                onClick={() => window.confirm(`Delete tenant "${t.name}"? Its jobs stay; users lose access to it.`) && run.mutate(() => deleteTenant(t.tenantId))}
              >
                <Trash2 className="h-4 w-4" />
              </Button>
            </div>
          ))}
          {tenants.length === 0 ? <p className="px-4 py-3 text-sm text-muted-foreground">No tenants yet.</p> : null}
        </div>
      </Section>

      <Section title="Users" description="Admins see every tenant, recurring schedules and workers. Other users see only their tenants' jobs. New users must change their password at first sign-in.">
        <form className="mb-4 grid gap-3" onSubmit={addUser}>
          <div className="grid gap-3 md:grid-cols-2">
            <div className="grid gap-1.5">
              <Label htmlFor="newUser">User name</Label>
              <Input id="newUser" value={userName} onChange={(e) => setUserName(e.target.value)} required minLength={3} maxLength={100} autoComplete="off" />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="newPassword">Initial password</Label>
              <Input id="newPassword" type="password" value={password} onChange={(e) => setPassword(e.target.value)} required minLength={8} autoComplete="new-password" />
            </div>
          </div>
          <label className="flex items-center gap-2 text-sm text-foreground">
            <input type="checkbox" checked={isAdmin} onChange={(e) => setIsAdmin(e.target.checked)} /> Admin (every tenant)
          </label>
          {!isAdmin ? <TenantChecklist tenants={tenants} value={userTenants} onChange={setUserTenants} /> : null}
          <div>
            <Button type="submit" disabled={run.isPending}>
              <Plus className="h-4 w-4" /> Add user
            </Button>
          </div>
        </form>

        <div className="overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-sm">
            <thead className="bg-muted/50 text-left text-muted-foreground">
              <tr>
                <th className="px-4 py-2 font-medium">User</th>
                <th className="px-4 py-2 font-medium">Access</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 font-medium">Created</th>
                <th className="px-4 py-2" />
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {(usersQuery.data ?? []).map((u) => (
                <tr key={u.id}>
                  <td className="px-4 py-2 font-medium text-foreground">{u.userName}</td>
                  <td className="px-4 py-2">
                    {u.isAdmin ? (
                      <span className="inline-flex items-center gap-1 text-foreground">
                        <ShieldCheck className="h-4 w-4 text-primary" /> Admin
                      </span>
                    ) : u.tenantIds.length ? (
                      u.tenantIds.map(tenantName).join(', ')
                    ) : (
                      <span className="text-muted-foreground">No tenants</span>
                    )}
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">
                    {u.lockedOut ? 'Locked out' : u.mustChangePassword ? 'Must change password' : 'Active'}
                  </td>
                  <td className="px-4 py-2 text-muted-foreground">{formatLocalDateTime(u.createdAtUtc)}</td>
                  <td className="px-4 py-2 text-right">
                    <Button type="button" size="sm" variant="outline" onClick={() => setEditing(u)}>
                      Edit
                    </Button>{' '}
                    {u.id !== me.id ? (
                      <Button
                        type="button"
                        size="icon"
                        variant="ghost"
                        aria-label={`Delete ${u.userName}`}
                        onClick={() => window.confirm(`Delete user "${u.userName}"?`) && run.mutate(() => deleteUser(u.id))}
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Section>

      {editing ? (
        <EditUser
          key={editing.id}
          user={editing}
          self={editing.id === me.id}
          tenants={tenants}
          busy={run.isPending}
          onCancel={() => setEditing(null)}
          onSave={(change) =>
            run.mutate(async () => {
              await updateUser(editing.id, change);
              setEditing(null);
            })
          }
        />
      ) : null}
    </div>
  );
}

function EditUser({
  user,
  self,
  tenants,
  busy,
  onCancel,
  onSave,
}: {
  user: DashboardUser;
  self: boolean;
  tenants: Tenant[];
  busy: boolean;
  onCancel: () => void;
  onSave: (change: { isAdmin: boolean; tenantIds: string[]; password?: string }) => void;
}) {
  const [isAdmin, setIsAdmin] = useState(user.isAdmin);
  const [tenantIds, setTenantIds] = useState(user.tenantIds);
  const [password, setPassword] = useState('');

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4" role="dialog" aria-modal="true">
      <form
        className="grid w-full max-w-lg gap-4 rounded-2xl border border-border bg-card p-6 shadow-lg"
        onSubmit={(e) => {
          e.preventDefault();
          onSave({ isAdmin, tenantIds: isAdmin ? [] : tenantIds, password: password || undefined });
        }}
      >
        <h3 className="text-base font-semibold text-foreground">Edit {user.userName}</h3>
        <label className="flex items-center gap-2 text-sm text-foreground">
          <input type="checkbox" checked={isAdmin} disabled={self} onChange={(e) => setIsAdmin(e.target.checked)} /> Admin (every tenant)
        </label>
        {!isAdmin ? <TenantChecklist tenants={tenants} value={tenantIds} onChange={setTenantIds} /> : null}
        <div className="grid gap-1.5">
          <Label htmlFor="resetPassword">
            <KeyRound className="mr-1 inline h-4 w-4" />
            Reset password (optional; they must change it at next sign-in)
          </Label>
          <Input id="resetPassword" type="password" value={password} onChange={(e) => setPassword(e.target.value)} minLength={8} autoComplete="new-password" />
        </div>
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="submit" disabled={busy}>
            Save
          </Button>
        </div>
      </form>
    </div>
  );
}