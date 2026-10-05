# Nexora VPS Production Bootstrap Record

## Status and evidence

- Updated: 2026-10-05 13:44:31 UTC (20:44:31 Asia/Saigon).
- Phase: **GHCR_IMAGE_PUBLISH_PREP_REQUIRED**. Operator completed deploy GHCR
  login; credential ownership/read scope verified. The owner package listing does
  not include nexora-backend and exact-main manifest is unavailable. A separately
  authorized safe publish-only path is needed. Render same-production-DB Worker
  still prohibits VPS application startup.
  Same-database Render Worker still prohibits VPS application startup.
- Repository: `qbao0111/nexora-backend`.
- Current reviewed main: `6d20de4392ca237457c27d310c3746b2ad07ee2d`.
- [PR #127](https://github.com/qbao0111/nexora-backend/pull/127): merged at
  `2026-10-05T13:18:29Z`, merge commit is the current main above. Only the bootstrap
  record changed since PR #126; deployment design is unchanged.
- [Current exact-main Backend CI 37315788736](https://github.com/qbao0111/nexora-backend/actions/runs/37315788736):
  completed successfully on `6d20de4392ca237457c27d310c3746b2ad07ee2d`.
- [PR #126](https://github.com/qbao0111/nexora-backend/pull/126): merged at
  `2026-10-05T12:29:50Z`; original bootstrap main was
  `d7b24a6852fea3aeed91eb8691bbe3faeab2b755`. Its tree matches
  reviewed PR HEAD `d1f97b87cfaadb306e4a46c63df508a34b9117ad`.
- [Backend CI run 37309933032](https://github.com/qbao0111/nexora-backend/actions/runs/37309933032):
  completed successfully on that exact main SHA.
- [Deploy production run 37310423818](https://github.com/qbao0111/nexora-backend/actions/runs/37310423818):
  skipped; no VPS deployment was performed.

Current versions of the deployment runbook, Compose definition, env example,
deploy script, Caddy example, production workflow, Dockerfile and container
entrypoint were inspected on this main. See
[VPS production deployment](VPS_PRODUCTION_DEPLOYMENT.md) for the authoritative
commands and deployment lifecycle. Re-fetch main and verify exact-head CI before
any eventual first deployment; this record is not approval to deploy an older SHA.

## GitHub configuration performed

| Item | Verified state |
| --- | --- |
| GitHub Environment | `production`, created |
| Allowed deployment branch | `main` only, custom branch policy |
| Required reviewers | `qbao0111`, `nbn1784` (second reviewer explicitly authorized by operator) |
| Prevent self-review | `true` |
| Administrator bypass | GitHub reports `can_admins_bypass=true`; not changed |
| Repository variable `PRODUCTION_DEPLOY_ENABLED` | `false` |
| Environment variable `VPS_USER` | `deploy` |
| Environment variable `VPS_HOST` | `180.93.59.22` |
| Environment variable `VPS_PORT` | `22` |
| Environment secret `VPS_SSH_PRIVATE_KEY` | Installed securely via stdin; value not logged |
| Environment secret `VPS_KNOWN_HOSTS` | Installed; trusted-console ED25519 host key pinned |

No existing branch protections were changed. Application credentials were not
stored in GitHub. The operator selected `nbn1784`; they were added without removing
qbao0111 or weakening self-review/main-only protection. A qbao0111-initiated release
can now be approved by the authorized second reviewer. No release approval was
requested or exercised, and the automatic deployment gate remains false.

## VPS and runtime evidence

Every pending item below requires privileged bootstrap or a later approved deploy.
Expected settings are **not** verification results.

| Item | Actual evidence / status |
| --- | --- |
| VPS hostname/IP; SSH port | `linux9484`, `180.93.59.22`, port `22` |
| Bootstrap/root access | Separate root bootstrap key installed by operator; pinned root SSH verified for host administration only |
| OS/version, kernel, architecture | Ubuntu 24.04.5 LTS noble, kernel 6.8.0-62-generic, x86_64 |
| CPU/RAM/disk | 2 CPUs; RAM 3.8 GiB, available 3.2 GiB; root disk 24 GB, free 19 GB; swap 4 GiB |
| Existing listeners | Public SSH 22 and Caddy HTTP 80; loopback DNS 53, X11 6010, Caddy admin 2019; no 443/10000 listener |
| Deploy user and effective Docker group | UID/GID 1000; docker group 988, verified in fresh SSH session |
| Docker Engine / Compose versions; Docker service | Engine 29.8.2; Compose v5.6.0; Docker active; deploy daemon access verified |
| Deployment public key and SHA256 fingerprint | Recorded below |
| VPS host public key SHA256 fingerprint | Recorded below, matched trusted operator root-session screenshots |
| Deploy SSH using pinned host key | PASS: BatchMode, IdentitiesOnly, StrictHostKeyChecking=yes |
| `/opt/nexora` and reviewed Compose/script | deploy:deploy mode 700 directory; executable deploy.sh 700, Compose 600 |
| `/opt/nexora/.env.production` | Operator populated; deploy:deploy mode 600 verified; required keys SET |
| `/root/nexora-secret-backup` and README | root:root directory 700; README 600, created |
| Private recovery files | Actions deployment key root:root mode 600; private-derived public fingerprint verified against deployed key |
| Caddy | v2.11.7 official apt package; enabled/active; package-default HTTP site only |
| Nexora Caddy config | `/etc/caddy/Caddyfile.nexora.staged`, root:root 644; validate PASS, NOT activated |
| Firewall | UFW active, deny incoming/routed, allow outgoing; IPv4/IPv6 TCP 22/80/443 allowed |
| GHCR package visibility / exact SHA image existence | Not listed in authenticated owner's container packages; exact-main registry manifest check HTTP 404; visibility not inferred |
| GHCR VPS pull authentication | Operator login complete; qbao0111 identity and read:packages verified; config directory 700/file 600 |
| Exact deployed main SHA | None |
| Container health / restart count / runtime non-root user | Not tested |
| API process / Worker process | Not started or verified |
| `/health/live` / DB readiness `/api/v1/health` | Not tested |
| Migrations / intended Neon production-v2 identity | Migrations NOT RUN; verified TLS read-only query and operator endpoint match PASS |
| Production Swagger 404 | Not tested on VPS |
| Provider startup validation | Not tested on VPS |
| Render service / Worker | Existing service and Worker are still running, per operator confirmation |
| Render Worker database overlap | Worker remains on the same production-v2 DB; BLOCKS VPS application startup |
| DNS cutover | **NOT PERFORMED** |
| Render Auto-Deploy | Manually disabled by the operator: `autoDeploy=no` / `autoDeployTrigger=off` |
| Render actions by Codex | No application deployment, environment-variable change, restart, suspension or DNS cutover |

The Render Auto-Deploy state above is operator-reported; no remote Render change
was made by Codex. Disabling Auto-Deploy only prevents future automatic deployments;
it does not stop the existing service or Worker. Same-production-DB Worker overlap
therefore still blocks VPS application startup.

Deployment public key (not a secret):

```text
ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIL8GVE8WZVdREQ6roPkEJXe82ympdBBdc/v1k/Qx0KpR nexora-github-actions-production
```

Deployment fingerprint: `SHA256:IwsV8Mr+l70JUY17IdI6udK2Vu54eJEnfb3ly3H1mks`.
VPS ED25519 host fingerprint:
`SHA256:WYLVP1BmmyEfpI5E1hbGiiRqpCb562OCySMDZyC9Mzw`.
The host public key was compared against the operator's authenticated root console
before pinned SSH login; keyscan alone was not treated as trusted authentication.

Compose/script copied from the reviewed main; remote SHA256 checksums:

- Compose: `994118ced08cf4a8b35aa0f943479455a85fa1487f3eb03921755c99430cf9d1`.
- deploy.sh: `1a9610057eeec89cd5d374cbde33985e9589a09fbe3d50696477cac2a560992c`.

No container was running at the initial automated inspection. The operator's
Docker installation reported a pending kernel reboot; no reboot was performed.

After the operator confirmed env input was complete, a metadata-only validation
script checked required keys without printing any values. DB, JWT, R2, Resend,
DeepSeek, payOS and Azure Speech required keys were all `SET`. Speech was enabled
by the operator. Production hosting modes, provider selectors and www/apex CORS
matched the requested configuration. This proves presence only, not credential
validity, JWT/Data Protection continuity, intended database identity or provider
connectivity. Env contents were never printed or downloaded. No app was started.
The private metadata-only helper is `/opt/nexora/validate-vps-env.sh`, mode `700`.
Main remained `d7b24a6852fea3aeed91eb8691bbe3faeab2b755`; its successful Backend CI
run above was rechecked before stopping on the Worker conflict.

Intended image reference for the current main inspected in this record:
`ghcr.io/qbao0111/nexora-backend:6d20de4392ca237457c27d310c3746b2ad07ee2d`.
This does not assert that the image exists or has been pulled. The current CLI
credential cannot list GHCR packages (`read:packages` is unavailable); a package
lookup returned 404, which does not establish public/private visibility. Do not
change package visibility or grant broad token scopes as a workaround. If it is
private, obtain a dedicated package-read credential securely, never through chat.

Compose source binds `127.0.0.1:10000:10000` and the Dockerfile uses a non-root user.
The workflow requires the deploy user, pinned known_hosts, successful main push CI
from this repository, current exact main SHA and the repository enable gate. It
copies only Compose/script, not application env. These are source checks; actual
VPS binding, user, migrations and readiness remain unverified.

## Bootstrap procedure and current stop boundary

Steps 1 through 6 below have completed except the root-only recovery backup.
The current stop is step 8: the operator confirms an active Render Worker on the
same database. No application startup, production migration or deployment command
has been executed. A privileged
operator has since authorized separate root bootstrap SSH and the root-only
recovery copy is complete. Deploy remains non-sudo; root is not used by CI or for
Nexora runtime operations.

1. Supply VPS host, SSH port and a secure bootstrap access method (an existing
   authorized SSH key/alias or trusted operator console). Do not paste root
   passwords, private keys or provider credentials into chat or tracked files.
2. Inspect OS/resources/listeners before modifying the VPS. Preserve existing
   services/data. Install Docker from the distribution's supported official apt
   repository, not a convenience installer.
3. Create/verify `deploy`, its SSH directory mode `700` and authorized_keys mode
   `600`. Independently test key login before considering any root/password SSH
   changes. Docker-group membership is effectively root-equivalent.
4. Generate the dedicated Actions key with comment
   `nexora-github-actions-production`, install the private key securely into
   `VPS_SSH_PRIVATE_KEY`, and pin the host key obtained from trusted server access
   in `VPS_KNOWN_HOSTS`. Record only public keys and fingerprints here.
5. Copy the reviewed Compose and executable deploy script to `/opt/nexora`.
   Generate `.env.production` from the **current** example and source: fill safe
   non-secret production values, leave secret values empty, explain operator
   inputs with comments, and set owner `deploy:deploy`, mode `600`. Do not overwrite
   an existing populated env file. Do not discover or export Render secret values.
6. **Stop after the empty env is created.** Only then report
   `ENV_FILE_READY_FOR_OPERATOR_INPUT`. The operator SSHs into the VPS and opens:

   ```bash
   nano /opt/nexora/.env.production
   # If logged in as root and sudo is available:
   sudo -u deploy nano /opt/nexora/.env.production
   ```

   Copy corresponding credentials manually from the operator's current production
   configuration, save, then tell Codex to continue. Do not paste credentials into
   chat/PR. The file has since been populated by the operator, mode `600`; do not
   print its contents.
7. After operator confirmation, check required keys with `SET`/`MISSING` output
   only, never values or resolved Compose configuration. Do not source the env file.
   Enabled-feature credentials must all be present before deployment. Speech needs
   an explicit enable/disable choice; the current template defaults to disabled.
   Keep the existing verified Resend sender, not the public support email. Reuse
   production JWT signing configuration; do not rotate it during migration.
   Verify Data Protection continuity separately before claiming existing Identity
   verification/reset tokens remain valid.
8. Before any startup, determine whether the old Render Worker uses the same Neon
   production-v2 database. If it would overlap, stop and request explicit cutover
   authorization. Do not stop Render automatically. The entrypoint performs
   migrations and starts **both** API and Worker, so startup is not a harmless probe.
9. Re-verify main, successful exact-head CI, registry access, SSH and all required
   configuration before an approved manual deployment through `deploy.sh`.
   Keep `PRODUCTION_DEPLOY_ENABLED=false` until separately approved. No DNS change,
   real payment charge, paid provider call or guessed authenticated account test.

## Recovery backup policy (VPS copy created)

The deployment key was generated outside git and its recovery copy is verified.
Retain the requested emergency
copy only in `/root/nexora-secret-backup` (directory mode `700`):

- `github-actions-deploy-ed25519`: private backup, mode `600`.
- `github-actions-deploy-ed25519.pub`: public key, separately stored.
- `README.txt`: file names/public fingerprints only; include:
  "DO NOT COMMIT. Copy this backup to qb's secure local storage and delete the VPS
  backup afterwards."

Never include private content here. qb must securely copy the backup off the VPS,
verify that copy, then authorize deletion of the VPS private backup. No automatic
deletion is approved in this phase; storage may not support reliable shredding.

## Rollback and emergency switches

No initial release or known-working rollback SHA exists on the VPS in this record.
After a deployment, retain the verified current/previous image records externally.
For an operator-approved, CI-passed, database-compatible previous SHA, as `deploy`:

```bash
cd /opt/nexora
bash deploy.sh <previous-compatible-full-40-character-SHA>
```

This is **not** a database rollback. Do not delete Data Protection volumes, run
unreviewed down migrations or restore the database blindly.

Emergency configuration switches are `Features__Speech=false`,
`Features__Ai=false`, `Features__Payment=false`. Changing env requires an approved
container recreation through the existing deploy procedure; editing alone has no
effect. Retain payOS credentials for existing-order reconciliation. These switches
are not global spend budgets and were not applied to any running service here.

## Security / validation boundary

This documentation contains no application env contents, credential values or
private keys. Only GitHub environment/protection configuration and non-secret
variables were changed, and dedicated SSH credentials installed securely in the
production environment. Reviewed deployment files and an empty env were provisioned
on the VPS. Codex performed no VPS application startup or production migration,
and no Render application deployment, environment-variable change, restart,
suspension or DNS cutover. The operator manually disabled Render Auto-Deploy
(`autoDeploy=no` / `autoDeployTrigger=off`); the existing Render service and Worker
remain running, so same-production-DB Worker overlap still blocks VPS startup.
Existing exact-main Backend CI success is recorded above, not claimed as VPS
health evidence.

## Earlier post-PR #127 preparation evidence (historical; superseded below)

- Operator subsequently authorized root-only host preparation while forbidding
  deploy sudo and Docker host-mount/privileged workarounds. A pinned BatchMode root
  SSH probe returned `Permission denied (publickey,password)`. Local SSH tooling
  has the dedicated deploy key only, no supplied root bootstrap key. The operator's
  MobaXterm root session exists, but native terminal control is unavailable to this
  execution session. Authorization is not authentication: root host tasks remain
  blocked. No root key was installed, root password requested, deploy sudo granted,
  SSH protection weakened or Docker permission workaround attempted. Current main
  remained `6d20de4392ca237457c27d310c3746b2ad07ee2d`. This initial authentication
  blocker was subsequently resolved by operator-installed separate root public key.

- GitHub production environment rechecked: main-only policy, SSH secret names
  present, host/port/user unchanged, repository enable gate remains `false`.
- **GITHUB_ENVIRONMENT_REVIEWER_BLOCKED**: only qbao0111 is configured as reviewer
  with self-review prevented. A deployment initiated by qbao0111 has no configured
  second reviewer. Existing write collaborators `onlyvu`, `dmm717`, `nbn1784` are
  candidates for operator selection, not automatically approved release reviewers.
  None was added; no reviewer, bypass or self-review protection was changed.
- Anonymous GHCR token/manifest probe returned HTTP 403. Visibility, package
  existence, exact-main tag and digest remain unverified; 403/404 alone are not
  treated as proof of private visibility or absence. VPS Docker credential config
  is absent; no login or pull was attempted without established registry access.
- **GHCR_IMAGE_PUBLISH_PREP_REQUIRED** if authenticated inspection confirms the
  exact SHA image is absent: the repository has only Backend CI and the gated
  build-and-deploy workflow, no publish-only action. Do not enable the deployment
  gate for publishing. Safest minimal proposal is a separately reviewed publish-only
  workflow using a short-lived package-write Actions token, green current main,
  production Dockerfile, linux/amd64, full SHA only, no SSH/startup and digest output.
  No such workflow was added/dispatched. Alternatively an operator-authorized build
  context can publish the exact main without repository changes; no authorized
  registry-write context was supplied here. Private VPS access needs a dedicated
  read-only package credential supplied securely, not in chat/argv/docs.
- Docker 29.8.2 / Compose v5.6.0 active and deploy daemon access rechecked. No VPS
  containers, including stopped containers, were listed. No Nexora image startup.
- Caddy executable/config absent. The reviewed example was staged at
  `/opt/nexora/Caddyfile.example`, not activated. Caddy installation/version/config
  validation remain pending root/operator access; deploy has no passwordless sudo.
  Follow [official Caddy Ubuntu installation](https://caddyserver.com/docs/install#debian-ubuntu-raspbian).
  Inspect and back up any existing `/etc/caddy/Caddyfile` before installation/config
  changes; preserve unrelated sites. The official package starts its service, so
  inspect listeners first and never activate the production hostname during prep.
- `sudo -n ufw status` could not run without operator credentials. Firewall status
  is **unknown**, not assumed disabled. No firewall rule or enable action occurred.
  Public listeners observed: TCP 22 on IPv4/IPv6; DNS 53 and X11 6010 are loopback.
  No listener on 80, 443 or 10000 was observed. Compose remains loopback-only for
  10000. A future root inspection must cover UFW and provider firewall rules.
- Safe future UFW commands, only after root confirms no unrelated-service conflict:
  `ufw allow 22/tcp`, `ufw allow 80/tcp`, `ufw allow 443/tcp`. Do not blindly enable
  UFW or remove unrelated rules. Preserve SSH and re-test pinned deploy SSH afterward.
  Never allow public 10000; Docker port bindings are a separate firewall boundary.
- DNS read-only lookup still resolves API CNAME to
  `nexora-backend-q32b.onrender.com`, through Render to `216.24.57.16`/`216.24.57.18`.
  DNS unchanged. Operator must confirm DNS-only versus proxied TLS strategy; no
  Cloudflare dashboard mode or origin certificate was inferred from this lookup.
- TLS pending: DNS-only needs public ACME after challenge routing reaches VPS;
  proxied needs valid origin TLS and Full (strict), plus trusted proxy/IP handling.
  Do not use Flexible. No origin certificate installed or ACME request triggered.
  After Caddy installation, run only `caddy validate --config
  /opt/nexora/Caddyfile.example --adapter caddyfile`; do not reload this staged site.
- Required DB/JWT/R2/DeepSeek/payOS/Resend/Azure keys again reported `SET`, env mode
  600 deploy:deploy. Hosting modes/providers/CORS/payOS callbacks and enabled Speech
  were checked by metadata-only validation. Actual Resend sender verification and
  credential validity remain unproven; env was not read back into chat or downloaded.
- Neon production-v2 remains operator-confirmed only. No PostgreSQL client was
  available on VPS and no database query/migration/write was executed. Before cutover,
  compare the configured endpoint in a secret-safe local check to the operator's
  non-secret Neon production-v2 endpoint ID. A read-only `current_database()` query
  alone cannot distinguish Neon branches sharing a database name; verify the endpoint
  mapping too. Never print connection strings or raw database errors.
- Render state is still operator-reported: Auto-Deploy disabled, live API/Worker on
  same production-v2. No Render API/CLI mutation, restart, deployment, env change,
  suspension or shutdown occurred in this phase.
- Root-only SSH recovery backup remains a manual privileged item. No sudo was granted,
  root key installed, or Docker root-equivalent workaround used to create it.

## Authorized root preparation completion (current evidence)

Root access was established only after the operator installed the separate
`nexora-root-bootstrap` public key in the existing root session. Key fingerprint:
`SHA256:1103LGU4T1N68yfbsIyZToKsIZzBFFi3QqswSkpPZ9Q`. Its private key stays outside
git with restricted local ACL, never in Actions or application env. Root and deploy
keys are separate; root SSH host key was pinned. Remove this temporary root
authorized-key entry only after operator approval and secure recovery/access review.
No root/password SSH policy was changed and deploy remains denied sudo.

1. Root inspection confirmed Ubuntu 24.04.5 x86_64, 2 CPUs, RAM 3.8 GiB (3.2 GiB
   available), disk 24 GB (19 GB free), swap 4 GiB. Docker active/enabled. Before
   changes, UFW was inactive with no added rules and listeners were SSH plus
   loopback DNS/X11 only; no unrelated public web service was present.
2. Installed official Caddy stable apt repository/package v2.11.7 and PostgreSQL
   client 16 for a read-only DB probe. No database server installed. No autoremove,
   distro upgrade or reboot. The package-default `/etc/caddy/Caddyfile` was backed
   up to `/root/nexora-host-config-backup/Caddyfile.package-default`.
3. Both active package-default config and staged reviewed Nexora config passed
   `caddy validate`. Caddy is enabled/active serving only its default HTTP site on
   port 80; the production hostname is **not loaded**. No ACME issuance attempted.
   Activation/TLS/DNS remain cutover tasks; preserve unrelated configs if added later.
4. UFW configured only after root inspection: allow TCP 22/80/443 on IPv4/IPv6,
   incoming/routed deny and outgoing allow. UFW enabled; fresh pinned root and deploy
   SSH both succeeded afterward. Current listeners are public TCP 22/80; Caddy admin
   2019, DNS and X11 stay loopback. No 10000 listener or allow rule exists; Compose
   remains `127.0.0.1:10000:10000`. Provider-edge firewall is not independently audited.
5. Root-only recovery copy created in directory mode 700. Private/public deployment
   key and README are root:root mode 600. Public key derived from private backup
   matches `SHA256:IwsV8Mr+l70JUY17IdI6udK2Vu54eJEnfb3ly3H1mks`; no private content
   was printed. README contains the required off-VPS verified-copy/deletion warning.
   Do not delete the requested copy automatically; operator must copy and verify it
   externally before authorizing deletion.
6. Operator explicitly chose `nbn1784`; their existing write access was confirmed,
   then added as second production reviewer. Main-only policy and self-review
   prevention unchanged; gate still false. Reviewer blocker is resolved, not bypassed.
7. Env presence/hosting/provider/CORS/payOS-callback/enabled-Speech checks pass, mode
   600 deploy:deploy. Initial DB TLS configuration was not VerifyFull. With explicit
   operator permission, changed **only** the SSL Mode parameter to VerifyFull via an
   atomic mode-600 update; did not rotate or modify credentials/host/database/JWT.
8. Read-only psql probe used system CA trust, verified hostname TLS, explicit
   `BEGIN READ ONLY`, transaction-local timeout, metadata SELECT and ROLLBACK. No
   writes/migrations. Initial probe failed before using pooler-compatible transaction
   setup; its provisional timeout category was not established as a server outage.
   Final probe passed TLS/read-only/current-database checks and matched the operator's
   `ep-muddy-poetry-b3wyjpga` endpoint (pooler suffix normalized). This verifies the
   supplied endpoint mapping, not an independent Neon management-plane branch audit.
   No connection string, password, raw errors or env contents were returned.
9. Initial GHCR checks lacked access. After operator completed deploy Docker login,
   server-side credential handling verified GitHub identity qbao0111 and read:packages
   scope without downloading/printing credentials. Complete authenticated owner
   container-package listing did not include nexora-backend; authenticated exact-main
   manifest check returned 404. No public/private visibility inferred from 404 alone.
   Credential config is deploy:deploy, directory 700/file 600. No image pull/publish,
   new workflow or deployment-gate enablement. **GHCR_IMAGE_PUBLISH_PREP_REQUIRED**:
   operator must approve a safe publish-only path before image creation; read-only
   VPS credential cannot publish and will not be broadened for that purpose.
10. Render untouched: operator reports Auto-Deploy off, API/Worker still running
    against the same DB. No VPS Nexora containers (running or stopped), application
    startup, EF bundle execution or DNS change. Docker daemon permissions for deploy
    verified again. Ordered cutover/rollback plan below remains approval-only.

## Authentication continuity findings

Source inspection: Identity uses default token providers and the configured
DataProtectionTokenProviderOptions lifespan (default 24 hours). No explicit shared
key-ring/application-name configuration was found. The Compose Data Protection
volume is new; no Render key ring was copied, and cross-environment/key-purpose
compatibility has not been established.

- JWT access tokens can remain valid until expiry only with matching signing key,
  issuer/audience and unchanged account/security-stamp state; actual cross-host
  values were not compared. No key rotation was performed.
- Refresh tokens are random opaque tokens hashed in the shared database, not
  Data Protection payloads (`IdentityAuthService`). Continuity requires the same DB,
  valid nonrevoked token/user state, unchanged public host/cookie path `/api/v1/auth`,
  secure cookie/CORS behavior and no concurrent Worker/API overlap. Not smoke-tested.
- Existing email verification and password reset links may fail on the new key ring;
  do not promise seamless validity. Either separately approve secure migration of a
  compatible ring/discriminator or plan user reissuance of links. Preserve the new
  VPS volume after first startup; do not delete it on rollback.

## Ordered cutover plan — approval only, NOT executed

Complete registry, reviewer, root/Caddy/firewall, TLS and DB identity prerequisites
above first. Root recovery backup and auth-link impact also need operator resolution.

1. Re-fetch exact main; require green exact-main Backend CI and reviewed deployment
   design. Current candidate is `6d20de4392ca237457c27d310c3746b2ad07ee2d`, not approval
   to deploy that SHA after main advances.
2. Establish authenticated registry visibility/tag existence. Publish only if safely
   authorized; record digest. As deploy, pull the exact image without running it and
   inspect only RepoDigests/user/image metadata, never embedded environment JSON.
3. Recheck env presence/semantics, intended Neon endpoint, backup/restore readiness,
   DB compatibility and provider sender/config. No paid-provider or payment smoke
   without separate authorization. Keep automatic deploy disabled.
4. Install Caddy through approved root access; back up prior config, validate staged
   Nexora config without reload/certificate issuance. Confirm firewall/TLS choice,
   current DNS target/TTL, forwarded-header policy and persistent key-ring handling.
5. Obtain explicit coordinated outage/cutover approval. Stop the production-facing
   Render compute using an operator-reviewed action; confirm BOTH its API and Worker
   are stopped and no other production-v2 worker/migrator is active. Do not treat
   Auto-Deploy off as stop evidence. **Steps 5 onward are outside this task.**
6. Only then, through pinned SSH as deploy:

   ```bash
   cd /opt/nexora
   bash deploy.sh 6d20de4392ca237457c27d310c3746b2ad07ee2d
   ```

   Substitute the newly reviewed exact SHA if main advanced; never use `latest`.
7. Require successful deploy exit, migration, Docker health and DB readiness. Verify
   local `/health/live`, `/api/v1/health`, both dotnet processes, non-root runtime,
   stable restart count and Swagger 404. Inspect/redact logs privately. On failure,
   hold DNS and follow rollback decision below.
8. After local success and TLS/proxy readiness, obtain/execute approved DNS cutover
   to VPS and activate reviewed Caddy config in the strategy-specific order. For
   DNS-only public ACME, challenge routing must reach VPS before issuance; for
   proxied mode install valid origin TLS before changing traffic and use Full (strict).
9. Test external HTTPS, unauthenticated auth behavior, approved real-account refresh,
   SignalR reconnect/REST reconciliation, provider configuration, jobs and Sentry.
   Record exact deployed image/digest, migration state, DNS/TLS evidence and durations.
10. Observe stable operation before approving old Render deletion or future automatic
    deploy enablement. Neither action is implied by this plan; gate stays `false` here.

## Rollback decision points

- Before Render stop: abort preparation with existing Render runtime unchanged.
- After Render stop but before successful VPS validation: stop failed VPS compute,
  confirm no VPS Worker/migrator remains, inspect whether migration committed any
  schema changes. Resume the preserved Render runtime ONLY with confirmed database/
  configuration compatibility and explicit approval; do not run both workers.
- After DNS cutover: retain previous DNS target/TTL evidence. Point DNS back only
  after compatible Render service is safely restored; account for cached routing.
  Drain/stop VPS processing first or explicitly coordinate an approved no-overlap
  recovery. DNS rollback alone does not stop a VPS Worker.
- For later image rollback use the documented deploy.sh path with a known-working,
  CI-passed compatible SHA. No such previous VPS release currently exists.
- Database migrations are not automatically reversible: require approved forward-fix
  or rehearsed recovery, never blind down migration/restore. Keep Data Protection
  volumes, inspect pending/in-flight jobs and idempotency state, and retain payOS
  reconciliation credentials for existing payments. No fabricated refunds or job
  replay policy is introduced. This is a plan, not a tested rollback rehearsal.
