import { useCallback, useEffect, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Layers, LogOut, Users } from 'lucide-react';

import { AUTH_EVENT, setSelectedTenant } from '@shared';
import { BackgroundJobsScreen } from './hcm/components/background-jobs/BackgroundJobsScreen';
import { BackgroundJobDetailView } from './hcm/components/background-jobs/BackgroundJobDetailView';
import type { BackgroundJobAccess } from './hcm/components/background-jobs/background-jobs-api';
import { Button } from './hcm/components/ui/button';
import { Select } from './hcm/components/ui/select';
import { fetchMe, logout, SYSTEM_TENANT, type Me } from './account-api';
import { ChangePasswordPage, LoginPage } from './AuthPages';
import { UsersPage } from './UsersPage';

// A signed-in user may act on every job the server lets them see; the server enforces the tenant scope.
const FULL_ACCESS: BackgroundJobAccess = { canView: true, canManage: true, canBulk: true, canViewPayload: true };

function useHashRoute() {
  const read = () => window.location.hash.replace(/^#\/?/, '');
  const [route, setRoute] = useState(read);
  useEffect(() => {
    const onChange = () => setRoute(read());
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, []);
  return route;
}

export function App() {
  const queryClient = useQueryClient();
  const [me, setMe] = useState<Me | null | undefined>(undefined);
  const [tenant, setTenant] = useState<string>('');
  const route = useHashRoute();

  const load = useCallback(() => {
    fetchMe()
      .then(setMe)
      .catch(() => setMe(null));
  }, []);

  useEffect(load, [load]);
  useEffect(() => {
    // A 401 (session ended) or a pending password change anywhere sends the user back to the right screen.
    window.addEventListener(AUTH_EVENT, load);
    return () => window.removeEventListener(AUTH_EVENT, load);
  }, [load]);

  const signedIn = (next: Me) => {
    queryClient.clear();
    setTenant('');
    setSelectedTenant(null);
    setMe(next);
  };

  if (me === undefined) {
    return <div className="flex min-h-screen items-center justify-center text-sm text-muted-foreground">Loading...</div>;
  }
  if (me === null) {
    return <LoginPage onSignedIn={signedIn} />;
  }
  if (me.mustChangePassword) {
    return <ChangePasswordPage me={me} onChanged={signedIn} onSignedOut={() => setMe(null)} />;
  }

  const tenantNames = new Map(me.tenants.map((t) => [t.tenantId, t.name]));
  const tenantName = (id: string | null) => (id === null || id === SYSTEM_TENANT ? 'System' : (tenantNames.get(id) ?? id));
  const tenantOptions = me.tenants.map((t) => ({ value: t.tenantId, label: t.name }));

  // Admins filter the lists by tenant; other users switch between their tenants (sent as a header, enforced by the server).
  const pickTenant = (value: string) => {
    setTenant(value);
    if (!me.isAdmin) {
      setSelectedTenant(value || null);
    }
    void queryClient.invalidateQueries();
  };

  const tenantPicker =
    tenantOptions.length > (me.isAdmin ? 0 : 1) ? (
      <div className="flex items-center gap-2">
        <span className="text-sm text-muted-foreground">Tenant</span>
        <Select
          value={tenant}
          options={tenantOptions}
          onChange={pickTenant}
          placeholder={me.isAdmin ? 'All tenants' : 'All my tenants'}
          className="w-56"
          aria-label="Tenant"
        />
      </div>
    ) : null;

  const isUsers = route === 'users' && me.isAdmin;
  const jobId = !isUsers && /^[0-9a-f-]{36}$/i.test(route) ? route : null;

  return (
    <div className="min-h-screen bg-muted/30">
      <header className="sticky top-0 z-40 border-b border-border bg-card/95 backdrop-blur">
        <div className="mx-auto flex max-w-[1500px] items-center gap-4 px-4 py-3">
          <a href="#/" className="flex items-center gap-2 font-semibold text-foreground">
            <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-primary text-primary-foreground">
              <Layers className="h-4 w-4" />
            </span>
            {me.title}
          </a>
          <nav className="flex items-center gap-1 text-sm">
            <a href="#/" className={`rounded-md px-3 py-1.5 ${!isUsers ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground'}`}>
              Jobs
            </a>
            {me.isAdmin ? (
              <a href="#/users" className={`rounded-md px-3 py-1.5 ${isUsers ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground'}`}>
                <Users className="mr-1 inline h-4 w-4" />
                Users & tenants
              </a>
            ) : null}
          </nav>
          <div className="ml-auto flex items-center gap-3">
            {!me.isAdmin ? tenantPicker : null}
            <span className="hidden text-sm text-muted-foreground sm:inline">
              {me.userName}
              {me.isAdmin ? ' (admin)' : ''}
            </span>
            <Button
              type="button"
              size="sm"
              variant="outline"
              onClick={async () => {
                await logout();
                queryClient.clear();
                setMe(null);
              }}
            >
              <LogOut className="h-4 w-4" /> Sign out
            </Button>
          </div>
        </div>
      </header>

      <main className="mx-auto grid max-w-[1500px] gap-4 px-4 py-6">
        {isUsers ? (
          <UsersPage me={me} />
        ) : jobId ? (
          <>
            <div>
              <a href="#/" className="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground">
                <ArrowLeft className="h-4 w-4" /> All jobs
              </a>
            </div>
            <BackgroundJobDetailView
              scope={me.isAdmin ? 'platform' : 'tenant'}
              jobId={jobId}
              basePath=""
              enabled
              access={FULL_ACCESS}
              tenantName={tenantName}
            />
          </>
        ) : (
          <BackgroundJobsScreen
            scope={me.isAdmin ? 'platform' : 'tenant'}
            basePath=""
            enabled
            access={FULL_ACCESS}
            tenantId={me.isAdmin ? tenant || null : null}
            tenantName={tenantName}
            tenantFilterSlot={me.isAdmin ? tenantPicker : null}
          />
        )}
      </main>
    </div>
  );
}