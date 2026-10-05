# VPS production deployment (prepare only)

This repository prepares **Linux amd64** deployments; it does not provision a VPS,
change DNS, configure GitHub secrets, approve a release, or deploy production.
Render staging remains separate and `render.yaml` is unchanged.
Production clients keep `https://api.nexorainterview.io.vn/api/v1`.

## Architecture and current gates

Internet → Cloudflare (optional proxy) → host Caddy HTTPS → `127.0.0.1:10000`
→ one non-root container supervising API + Worker. PostgreSQL stays on Neon,
files on private R2, Speech on Azure; AI, Resend and payOS remain external.
No local database is created. One Docker volume preserves ASP.NET Core Identity
Data Protection keys across recreations (do not delete it during rollback).
Back up and protect that volume: keys are stored on disk, not encrypted by this PR.

**Approved adapters, separate go-live obligations:** ProductionSafety permits only
R2 storage, enabled DeepSeek AI and enabled payOS payment. The example enables AI,
payment and uploads, with Speech conservatively off. Required provider options still
validate on startup. A healthy container proves configuration/startup, not successful
provider traffic or legal/cost readiness. Monetary budgets/automatic spend alerts and
refund/invoice/tax policies are not implemented by this corrective; operators must
review the [ADR control audit](07-architecture-decisions.md#pr-126-production-approval-and-control-audit)
and [production runbook](04-production-runbook.md) before real traffic.

Audit findings: the old image forced Staging, entrypoint silently skipped missing
migration/DB configuration, used root and world-writable local storage, and passed
the connection string in migration argv. The neutral entrypoint requires matching
hosting environments, DB configuration and executable bundle, runs migrations
before either process, handles TERM/INT and stops the sibling on any child exit.
Successful unexpected exits also fail the container. Startup/shutdown logs contain
no configuration values. Docker's 60-second grace bounds shutdown before SIGKILL.
Render must continue supplying both Staging environment variables, connection
string and R2/email configuration; existing Blueprint already declares the modes.
Default local scratch is still `/tmp/nexora-storage`, not durable production storage.

## 1. Operator-only bootstrap

Use an approved amd64 Linux VPS, supported Ubuntu release, backup policy and firewall.
Install Docker Engine and Compose plugin via the
[official apt instructions](https://docs.docker.com/engine/install/ubuntu/), not
a blind convenience script. Install Caddy via its official distribution instructions,
plus `curl`, `util-linux` (`flock`), and OpenSSH. Verify `docker compose version`.
An administrator runs once:

```bash
sudo adduser --disabled-password --gecos '' deploy
sudo usermod -aG docker deploy
sudo install -d -o deploy -g deploy -m 700 /home/deploy/.ssh /opt/nexora
sudo install -o deploy -g deploy -m 600 /dev/null /home/deploy/.ssh/authorized_keys
```

Docker group membership is effectively root-equivalent on the host: dedicate this
user/key to releases, restrict SSH ingress where possible and don't grant routine
interactive access or unnecessary sudo. Relogin after group changes. SSH as **deploy**,
never root. Allow approved SSH port, 80 and 443; do not open 10000 or Neon to everyone.
Docker published ports can bypass host firewall rules; keep the loopback bind.

Generate a dedicated key on a trusted operator workstation, e.g.
`ssh-keygen -t ed25519 -f nexora-production-deploy` (CI requires noninteractive key).
Install only its public key in `authorized_keys`, preferably with
`restrict` to disable forwarding/PTY. Protect the private key; never commit either
application credentials or private keys. No key rotation is performed by this PR.

Pin host keys after verifying the VPS fingerprint through a trusted console or
out-of-band administrator channel. `ssh-keyscan -p <port> <host>` can format a
known-hosts entry but is **not** authentication by itself. For nonstandard ports the
entry uses `[host]:port`. Never use `StrictHostKeyChecking=no`.

## 2. GitHub protection and registry

Create GitHub Environment **production**, restrict deployment branches to `main`,
require a human reviewer, and prevent self-review where available. Confirm the
repository plan supports the chosen environment protections before enabling releases.
Environment variables: `VPS_HOST` (DNS name/IPv4), `VPS_PORT`, `VPS_USER=deploy`.
Environment secrets: `VPS_SSH_PRIVATE_KEY`, `VPS_KNOWN_HOSTS` (verified entry).
Repository variable: `PRODUCTION_DEPLOY_ENABLED=true` **only after bootstrap, gates
and protection are reviewed**. Absent/false leaves automated deployment disabled.
Never put application credentials in workflow variables.

Actions uses its short-lived `GITHUB_TOKEN` (`contents:read`, `packages:write`) to
publish `ghcr.io/qbao0111/nexora-backend:<full-40-character-main-SHA>`. No latest tag.
Do not overwrite/delete published release SHA tags; reruns reuse an existing tag.
Restrict package writers and retention so previous working releases remain available.
GHCR tags are not registry-enforced immutable; enforce this operational policy.

For a private package, log in on the VPS **as deploy**, once, using a dedicated
least-privilege account/token with only `read:packages` and access to this package
(authorize SSO if required). No broad repo/admin PAT and no write token on the VPS.
Use interactive `docker login ghcr.io -u <package-reader>` and paste the token at
the password prompt, or a secret-manager-fed `--password-stdin`; don't put it in argv.
Protect `/home/deploy/.docker/config.json` mode 600 or use a credential helper.
See [GHCR authentication](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).

## 3. Server files and application configuration

Copy reviewed `deploy/docker-compose.production.yml` and `deploy/deploy.sh` to
`/opt/nexora/`. Copy `deploy/.env.production.example` to `.env.production` there;
edit privately on the server, set ownership `deploy:deploy` and mode **600**.
The workflow updates Compose/script only, never rewrites application env/secrets.
Do not `source` the file: it is Compose env-file syntax, not shell commands.
Use single quotes for secrets containing `$` to avoid Compose interpolation;
test configuration with `config --quiet`, never print resolved production config.

The template uses current source keys, not old `DATABASE_URL`/`AUTH_SECRET` aliases:

| Area | Configuration |
| --- | --- |
| Host/DB | Both environment keys `Production`; `ConnectionStrings__Postgres` is Npgsql connection syntax to Neon, TLS `SSL Mode=VerifyFull` |
| Auth | `Authentication__Jwt__*` (strong random signing key); secure Strict refresh cookie; actual HTTPS verification frontend origin |
| CORS | Indexed `Frontend__AllowedOrigins__0/1` www and apex; no wildcard with credentials |
| Storage | `Storage__Provider=r2` and all `Storage__R2__*` private bucket credentials/HTTPS endpoint |
| AI | `Features__Ai=true`, `Ai__Provider=deepseek`, `Ai__DeepSeek__*`; model/reasoning/retry defaults unchanged; Gemini is not approved in Production |
| Speech | `Features__Speech`, `Speech__Azure__*`; set false and recreate container to disable temporarily |
| Email | `Email__Provider=resend`, `Email__FromAddress/FromName`, `Email__Resend__*`; existing verified sender, not the Gmail public contact |
| Payment | `Features__Payment=true`, `Billing__Payment__Provider=payos`, `Billing__Payos__*`; public HTTPS return `/payment/success`, cancel `/payment/cancel` |
| Monitoring | Optional `Sentry__Dsn`; Compose sets release to `nexora-backend@<SHA>` for API and Worker |
| Background | `Realtime__*`, `Worker__Polling__*`; existing defaults, no job/retry policy changes |

API startup validates email even if AI/payment are disabled. Enabled DeepSeek/payOS
require valid credentials/options; their credentials are not required for startup
when the corresponding Production feature is disabled. Unknown selectors still fail
configuration validation. Never treat placeholders
as a usable production setup. Keep existing private privacy/retention feature settings;
do not enable purge as part of infrastructure cutover. JWT credentials must be stable
through cutover; changing them signs users out. Review Neon connection limits and
network access, R2 CORS/signed upload policy, and provider callback domains.

Callback paths were verified read-only in local `nexora-fe` source
(`src/app/payment/success/page.tsx`, `src/app/payment/cancel/page.tsx`);
`/billing` is only the legacy compatibility page. Operator must verify the deployed
FE has both routes on the configured HTTPS origin before enabling real checkout.
Redirect/query parameters are never proof of payment; backend reconciliation remains
authoritative. No FE changes or live callback registration are performed here.

Emergency switches: set `Features__Ai=false`, `Features__Payment=false` and/or
`Features__Speech=false`, then **recreate/redeploy the container**, even at the same
verified SHA. Editing env alone has no effect. AI pause blocks new API work and
queued worker AI processing without failing/refunding jobs; privacy still runs.
Payment pause blocks new checkout, but existing orders still need verified callbacks
and query reconciliation: retain valid payOS credentials. Speech uses existing Azure
validation; default is false, operator may enable it with approved Azure configuration.
Monitor provider balance/usage separately; quotas/rate limits are not a global monetary
budget or automatic spend breaker. Keep `RateLimits:Disabled=false` for production.

## 4. First manual release and automated releases

First release is operator-only, **after successful Backend CI on the chosen main SHA**
and approval. If no image exists yet, an authorized operator can build/publish that
exact checkout with the same Dockerfile and SHA naming. Do not tag an untested checkout.
Ensure the old production Worker is stopped before starting a new one on the same DB.
Render **staging** may keep running with its separate DB/bucket.

```bash
cd /opt/nexora
bash deploy.sh <full-reviewed-successful-CI-main-SHA>
curl --fail http://127.0.0.1:10000/health/live
curl --fail http://127.0.0.1:10000/api/v1/health
```

`Deploy production` listens to **Backend CI**, only successful **push** runs on this
repository's main, not PR/fork/manual CI. It checks the exact run SHA against main,
waits for production environment approval, builds/pushes SHA image, rechecks main,
uses pinned SSH and transfers definitions, then runs `deploy.sh`. A superseded run
is skipped. Actions concurrency never interrupts an in-progress migration.
The server's `flock` serializes manual/automated releases too.

`deploy.sh` validates SHA/env permissions, pulls before downtime, records the previous
image, stops the old container, starts one new container, then waits up to six minutes
for Docker liveness **and** DB readiness. This is deliberately not zero-downtime.
Failed pull leaves old runtime intact; failed migration/start/health fails release,
does not report success, and requires operator recovery. No automatic DB downgrade.
Docker marks unhealthy but does not automatically restart on that alone;
`unless-stopped` restarts exited containers. API/Worker child exits cause container exit.
Logs are bounded (3 × 10MB). Monitor startup failures and restart loops externally.

## 5. Migrations and shutdown

One container runs the bundled EF migrator once per startup/restart, before either
application process. It reads the connection from environment, not command arguments.
Applied migrations are idempotently checked; missing/failed migration aborts startup.
No schema/migration changes are made in this infrastructure PR. Back up Neon and
review compatibility before approving every release. Never run another migrating
container/old Render instance against the same DB concurrently. The server lock does
not coordinate other hosts. Future split API/Worker requires one dedicated migration
release job and removing migrations from both process entrypoints.
TERM/INT stop migrator or both application children and wait; Docker bounds that wait.
No guarantee is made that liveness proves job processing/provider health.

## 6. Reverse proxy and cutover (DO NOT execute from this PR)

Install the reviewed `deploy/Caddyfile.example` in the host Caddy config, validate
with `caddy validate`, then reload only after approval. Caddy supports SignalR
WebSocket upgrades without custom upgrade-header rules; REST is still authoritative.
No access logging of query tokens is enabled by this example.

- DNS-only: point approved DNS to VPS, allow 80/443; Caddy obtains/renews publicly
  trusted HTTPS certificates. Test challenge reachability first.
- Cloudflare proxied: configure a valid origin certificate (publicly trusted Caddy
  certificate with reachable ACME challenge, or separately installed Cloudflare Origin
  CA certificate/key), set **Full (strict)**, never Flexible. With Origin CA replace
  Caddy automatic TLS with `tls /secure/path/origin.pem /secure/path/origin.key`,
  protect private key and allow only approved origin ingress. No cert/key in git.

Existing API forwarded-header middleware clears known proxy/network lists; this PR
does not change that behavior. The security boundary is the loopback-only port and
trusted host Caddy. Caddy defaults do not trust incoming spoofed forwarding headers.
In proxied mode configure Caddy's `trusted_proxies` with verified, maintained Cloudflare
CIDRs and strict right-to-left parsing; never trust all networks. Otherwise client IP
rate limiting sees Cloudflare rather than the real client. Verify original scheme,
host and client-IP handling before cutover; never expose Docker 10000 publicly.
Swagger/OpenAPI is not mapped in Production; `/health/live` is public, intentionally
checks process only; `/api/v1/health` checks DB and operations endpoint reports status.

Cutover checklist (operator must record evidence, not just tick based on build):

1. VPS container healthy; both processes present, no restart loop.
2. Neon readiness, backup/restore rehearsal and schema compatibility.
3. Private R2 upload/download and owner isolation.
4. Verification/reset email with approved verified Resend sender.
5. Azure Speech token flow if enabled; otherwise explicitly disabled.
6. Approve cost exposure/monitoring and kill-switch response; verify actual DeepSeek model/valid structured output.
7. Approve applicable financial/legal policies; verify payOS checkout/signature/idempotent webhook and callback URLs.
8. Worker handles real approved test jobs, queue/operations healthy.
9. Authenticated SignalR reconnect/event → REST reconciliation.
10. Stop old production compute/Worker using this DB before overlap; retain separate Render staging.
11. Approve Cloudflare DNS cutover to VPS; verify TLS mode/certificate/IP headers.
12. Verify `https://api.nexorainterview.io.vn/health/live` and DB readiness.
13. Smoke web and mobile without client URL changes (login/refresh/upload/interview/report).
14. Monitor private container logs/Sentry release, requests, jobs and provider failures.

## 7. Rollback, logs and recovery

Before each release `/opt/nexora/previous-image.txt` records the prior image reference;
`current-image-sha.txt` records only the last verified SHA. Retain reviewed release
records externally, as previous-image.txt is replaced on the next attempt.
Choose a known-working SHA whose Backend CI passed and DB compatibility is established:

```bash
cd /opt/nexora
bash deploy.sh <previous-compatible-full-SHA>
```

This reuses the lock, pull/stop/start/health checks. **Container rollback != database
rollback.** The older bundle does not safely reverse newer schema; use a reviewed
forward-fix or separately rehearsed database recovery plan. Do not delete volumes,
run unreviewed down migrations or restore production DB blindly. Keep previous images.

```bash
cd /opt/nexora
export NEXORA_IMAGE_TAG=<current-image-SHA>
docker compose -f docker-compose.production.yml ps
docker compose -f docker-compose.production.yml logs --tail 100 nexora
```

Logs are private operational data; inspect locally, redact before sharing. Do not
print env, `docker inspect` full JSON or resolved Compose config into CI/tickets.
For failures check registry read access, pinned SSH key, env mode, ProductionSafety
flags, migration connectivity, DB readiness and provider options. Changing env alone
does not update running containers: deploy the same verified SHA to recreate them.
The entrypoint rename and required host environments are the only Render-related
change; verify staging health after its next manually approved deployment.

## Validation boundaries

Backend CI includes standard .NET/PostgreSQL tests plus Bash lifecycle regressions,
Compose validation, real Docker build and a Production-mode container smoke against
disposable PostgreSQL, with AI=true/deepseek, payment=true/payos and upload=true/r2.
It uses test-only placeholder credentials and an internal Docker network with no
outbound route (no external provider calls). Production API startup regressions also
verify invalid/missing DeepSeek/payOS options fail via ValidateOnStart. That does not
prove Neon/R2/Azure/AI/Resend/payOS or VPS/DNS deployment.
Deployment remains disabled until explicit operator setup/approval; no deployment
or DNS action is executed when this PR is created.
