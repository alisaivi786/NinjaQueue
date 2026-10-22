import { useState, type FormEvent, type ReactNode } from 'react';
import { KeyRound, Layers } from 'lucide-react';

import { errorMessage } from '@shared';
import { Button } from './hcm/components/ui/button';
import { Input } from './hcm/components/ui/input';
import { Label } from './hcm/components/ui/label';
import { changePassword, login, logout, type Me } from './account-api';

function AuthCard({ title, subtitle, children }: { title: string; subtitle: string; children: ReactNode }) {
  return (
    <div className="flex min-h-screen items-center justify-center bg-muted/40 px-4">
      <div className="w-full max-w-sm rounded-2xl border border-border bg-card p-8 shadow-sm">
        <div className="mb-6 flex items-center gap-3">
          <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-primary text-primary-foreground">
            <Layers className="h-5 w-5" />
          </div>
          <div>
            <h1 className="text-lg font-semibold text-foreground">{title}</h1>
            <p className="text-sm text-muted-foreground">{subtitle}</p>
          </div>
        </div>
        {children}
      </div>
    </div>
  );
}

function Field({ id, label, children }: { id: string; label: string; children: ReactNode }) {
  return (
    <div className="grid gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      {children}
    </div>
  );
}

export function LoginPage({ onSignedIn }: { onSignedIn: (me: Me) => void }) {
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      onSignedIn(await login(userName, password));
    } catch (e) {
      setError(errorMessage(e, 'Sign-in failed.'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthCard title="NinjaQueue" subtitle="Sign in to the jobs dashboard">
      <form className="grid gap-4" onSubmit={submit}>
        <Field id="userName" label="User name">
          <Input id="userName" autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} autoFocus required />
        </Field>
        <Field id="password" label="Password">
          <Input id="password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        </Field>
        {error ? <p className="text-sm text-destructive">{error}</p> : null}
        <Button type="submit" disabled={busy}>
          {busy ? 'Signing in...' : 'Sign in'}
        </Button>
      </form>
    </AuthCard>
  );
}

export function ChangePasswordPage({ me, onChanged, onSignedOut }: { me: Me; onChanged: (me: Me) => void; onSignedOut: () => void }) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (next !== confirm) {
      setError('The new passwords do not match.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      onChanged(await changePassword(current, next));
    } catch (e) {
      setError(errorMessage(e, 'The password could not be changed.'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthCard title="Choose a new password" subtitle={`${me.userName} - required before you continue`}>
      <form className="grid gap-4" onSubmit={submit}>
        <Field id="current" label="Current password">
          <Input id="current" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />
        </Field>
        <Field id="next" label="New password (at least 8 characters)">
          <Input id="next" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} required minLength={8} />
        </Field>
        <Field id="confirm" label="Confirm new password">
          <Input id="confirm" type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required minLength={8} />
        </Field>
        {error ? <p className="text-sm text-destructive">{error}</p> : null}
        <Button type="submit" disabled={busy}>
          <KeyRound className="h-4 w-4" /> {busy ? 'Saving...' : 'Change password'}
        </Button>
        <Button
          type="button"
          variant="ghost"
          onClick={async () => {
            await logout();
            onSignedOut();
          }}
        >
          Sign out
        </Button>
      </form>
    </AuthCard>
  );
}