// Demo Shop: each card posts to /demo/... and the jobs it creates show up in the NinjaQueue dashboard at /jobs.
(function () {
  'use strict';

  const scenarios = [
    {
      tag: 'Fire-and-forget',
      title: 'Customer signs up',
      text: 'Enqueues a welcome email. It runs within a second or two on the "email" queue.',
      watch: 'Dashboard → Jobs: Shop.SendWelcomeEmail.v1 goes Enqueued → Processing → Succeeded.',
      actions: [{ label: 'Sign up a customer', path: 'signup', body: () => ({ email: `user${Date.now() % 10000}@example.com` }) }],
      code: `await jobs.EnqueueAsync(
    new SendWelcomeEmail(email),
    new JobOptions { TenantId = tenantId });`,
    },
    {
      tag: 'Outbox + continuation',
      title: 'Place an order',
      text: 'Saves the order and stages three jobs in the same database transaction: confirmation email, payment, and shipping that waits for the payment.',
      watch: 'Shipping shows "Awaiting parent" until the payment succeeds, then runs. Open a job to see its attempts and events.',
      actions: [
        { label: 'Place order', path: 'checkout', body: () => ({ customer: randomName(), total: 49.9 }) },
        { label: 'Place order, then roll back', path: 'checkout', kind: 'ghost', body: () => ({ customer: randomName(), total: 19.9, rollback: true }) },
      ],
      code: `await using var tx = await db.Database.BeginTransactionAsync();
db.Orders.Add(order);
await db.SaveChangesAsync();
await jobs.StageAsync(new SendOrderConfirmation(order.Id, email), options);
var payment = await jobs.StageAsync(new ChargePayment(order.Id, total, false), options);
await jobs.StageAsync(new ShipOrder(order.Id), options with { ContinueAfter = payment });
await tx.CommitAsync();   // order + 3 jobs, or nothing at all`,
    },
    {
      tag: 'Retries + dead letter',
      title: 'Card gets declined',
      text: 'The payment fails 3 times (with back-off) and lands in dead letters; shipping keeps waiting.',
      watch: 'Filter Jobs by "Dead-lettered", open the payment job and press Retry: attempt 4 succeeds and shipping runs.',
      actions: [{ label: 'Order with a declined card', path: 'checkout', kind: 'danger', body: () => ({ customer: randomName(), total: 120, simulateDecline: true }) }],
      code: `[JobType("Shop.ChargePayment.v1", MaxAttempts = 3, ...)]
public sealed class ChargePaymentHandler : IJobHandler<ChargePayment>
{
    public async Task<JobResult> HandleAsync(ChargePayment p, JobContext ctx, CancellationToken ct)
    {
        if (p.SimulateDecline && ctx.Attempt <= 3)
            throw new InvalidOperationException("Card declined");   // retried, then dead-lettered
        ...
    }
}`,
    },
    {
      tag: 'Delayed',
      title: 'Abandoned cart reminder',
      text: 'Schedules a reminder email to run in one minute.',
      watch: 'The job sits in "Scheduled" with its run time, then runs on time. You can cancel it before then.',
      actions: [
        { label: 'Remind in 1 minute', path: 'reminder', body: () => ({ delaySeconds: 60 }) },
        { label: 'Remind in 10 minutes', path: 'reminder', kind: 'ghost', body: () => ({ delaySeconds: 600 }) },
      ],
      code: `await jobs.EnqueueAsync(new SendCartReminder(email),
    new JobOptions { TenantId = tenantId, RunAt = DateTimeOffset.UtcNow.AddMinutes(1) });`,
    },
    {
      tag: 'Long-running',
      title: 'Import the product catalog',
      text: 'A 30-second import that reports its progress as it goes.',
      watch: 'The job shows a progress bar while Processing. Cancel it from the job page to stop it mid-way.',
      actions: [{ label: 'Import 60 products', path: 'import', body: () => ({ products: 60 }) }],
      code: `for (int i = 1; i <= total; i++)
{
    await ImportProductAsync(i, ct);
    await ctx.ReportProgressAsync(i, total, ct);
}`,
    },
    {
      tag: 'Load',
      title: 'Marketing blast',
      text: 'Enqueues 200 welcome emails in one round trip and lets the workers drain them.',
      watch: 'Dashboard → Jobs: the figures and the "email" queue depth rise, then fall back to zero.',
      actions: [{ label: 'Send 200 emails', path: 'burst', body: () => ({ count: 200 }) }],
      code: `await jobs.EnqueueManyAsync(customers
    .Select(c => (new SendWelcomeEmail(c.Email), (JobOptions?)new JobOptions { TenantId = tenantId }))
    .ToList());`,
    },
    {
      tag: 'Recurring',
      title: 'Sales report every minute',
      text: 'A platform job on a cron schedule, registered at startup. Nothing to click here: it already runs.',
      watch: 'Sign in as admin → Recurring schedules: pause it, resume it, or "Run now". Its runs appear under Jobs.',
      actions: [],
      code: `builder.Services.AddNinjaQueue(builder.Configuration)
    .UsePostgreSql<DemoDbContext>()
    .AddJobHandlersFromAssembly(typeof(Program).Assembly)
    .AddRecurring("shop.sales-report", "* * * * *", "UTC", new SalesReport());`,
    },
  ];

  const tenantSelect = document.getElementById('tenant');
  const log = document.getElementById('log');
  const ordersBody = document.getElementById('orders');
  let tenants = [];

  function randomName() {
    const names = ['Ayesha Khan', 'Bilal Ahmed', 'Sara Malik', 'Omar Farooq', 'Hina Raza', 'Usman Ali', 'Zara Shah'];
    return names[Math.floor(Math.random() * names.length)];
  }

  function tenantId() {
    return tenantSelect.value;
  }

  function addLog(text, kind) {
    const item = document.createElement('li');
    item.className = kind || '';
    const time = document.createElement('time');
    time.textContent = new Date().toLocaleTimeString();
    item.append(time, document.createTextNode(' ' + text));
    log.prepend(item);
  }

  async function post(path, body) {
    const response = await fetch(`demo/${path}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ tenantId: tenantId(), ...body }),
    });
    if (!response.ok) {
      throw new Error(`${response.status} ${await response.text()}`);
    }
    return response.json();
  }

  function render() {
    const host = document.getElementById('cards');
    const template = document.getElementById('card');
    for (const scenario of scenarios) {
      const card = template.content.cloneNode(true);
      card.querySelector('.card-tag').textContent = scenario.tag;
      card.querySelector('h3').textContent = scenario.title;
      card.querySelector('.card-text').textContent = scenario.text;
      card.querySelector('.card-watch').textContent = scenario.watch;
      card.querySelector('code').textContent = scenario.code;
      const actions = card.querySelector('.card-actions');
      for (const action of scenario.actions) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = `button ${action.kind || 'primary'}`;
        button.textContent = action.label;
        button.addEventListener('click', async () => {
          button.disabled = true;
          try {
            const result = await post(action.path, action.body());
            const tenant = tenants.find((t) => t.tenantId === tenantId());
            if (result.rolledBack) {
              addLog(`${scenario.title}: rolled back. No order and no jobs exist.`, 'muted');
            } else {
              const count = Array.isArray(result.jobs) ? result.jobs.length : result.jobs;
              addLog(`${scenario.title}: ${count} job(s) created for ${tenant ? tenant.name : 'tenant'}.`, 'ok');
            }
            loadOrders();
          } catch (error) {
            addLog(`${scenario.title} failed: ${error.message}`, 'error');
          } finally {
            button.disabled = false;
          }
        });
        actions.append(button);
      }
      host.append(card);
    }
  }

  async function loadOrders() {
    const response = await fetch(`demo/orders?tenantId=${encodeURIComponent(tenantId())}`);
    const orders = response.ok ? await response.json() : [];
    ordersBody.replaceChildren(
      ...orders.map((o) => {
        const row = document.createElement('tr');
        for (const value of [o.id.slice(0, 8), o.customer, `$${Number(o.total).toFixed(2)}`, o.status, new Date(o.createdAtUtc).toLocaleTimeString()]) {
          const cell = document.createElement('td');
          cell.textContent = value;
          row.append(cell);
        }
        row.children[3].className = `status ${o.status.toLowerCase()}`;
        return row;
      }),
    );
    if (orders.length === 0) {
      const row = document.createElement('tr');
      const cell = document.createElement('td');
      cell.colSpan = 5;
      cell.className = 'empty';
      cell.textContent = 'No orders yet. Place one above.';
      row.append(cell);
      ordersBody.append(row);
    }
    const tenant = tenants.find((t) => t.tenantId === tenantId());
    document.getElementById('orders-tenant').textContent = tenant ? `· ${tenant.name}` : '';
  }

  async function init() {
    const info = await (await fetch('demo/info')).json();
    document.getElementById('db').textContent = info.database;
    tenants = info.tenants;
    for (const t of tenants) {
      const option = document.createElement('option');
      option.value = t.tenantId;
      option.textContent = t.name;
      tenantSelect.append(option);
    }
    tenantSelect.addEventListener('change', loadOrders);
    document.getElementById('clear-log').addEventListener('click', () => log.replaceChildren());
    document.getElementById('refresh-orders').addEventListener('click', loadOrders);
    render();
    await loadOrders();
    setInterval(loadOrders, 3000);
  }

  init().catch((error) => addLog(`Could not load the demo: ${error.message}`, 'error'));
})();