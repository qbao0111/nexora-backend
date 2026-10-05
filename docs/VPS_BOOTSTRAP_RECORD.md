# Nexora VPS Production Bootstrap Record

## Status and evidence

- Updated: 2026-10-05 13:02:27 UTC (GitHub environment created at 12:39:29 UTC).
- Phase: **BLOCKED_INFRA_FAILURE** (deployment safety conflict, not an observed
  VPS runtime failure). Operator completed env input; same-database Render Worker
  remains running, so VPS startup is prohibited pending explicit coordination.
- Repository: `qbao0111/nexora-backend`.
- Reviewed main: `d7b24a6852fea3aeed91eb8691bbe3faeab2b755`.
- [PR #126](https://github.com/qbao0111/nexora-backend/pull/126): merged at
  `2026-10-05T12:29:50Z`; merge commit is the reviewed main above. Its tree matches
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
| Required reviewer | `qbao0111` |
| Prevent self-review | `true` |
| Administrator bypass | GitHub reports `can_admins_bypass=true`; not changed |
| Repository variable `PRODUCTION_DEPLOY_ENABLED` | `false` |
| Environment variable `VPS_USER` | `deploy` |
| Environment variable `VPS_HOST` | `180.93.59.22` |
| Environment variable `VPS_PORT` | `22` |
| Environment secret `VPS_SSH_PRIVATE_KEY` | Installed securely via stdin; value not logged |
| Environment secret `VPS_KNOWN_HOSTS` | Installed; trusted-console ED25519 host key pinned |

No existing branch protections were changed. Application credentials were not
stored in GitHub. With self-review prevention, a release initiated by the only
reviewer may need another authorized reviewer; resolve this before enabling
automatic deployments, without weakening the protection. No second reviewer was
assumed or added.

## VPS and runtime evidence

Every pending item below requires privileged bootstrap or a later approved deploy.
Expected settings are **not** verification results.

| Item | Actual evidence / status |
| --- | --- |
| VPS hostname/IP; SSH port | `linux9484`, `180.93.59.22`, port `22` |
| Bootstrap/root access | Operator root MobaXterm session; automated SSH is deploy-only, no sudo |
| OS/version, kernel, architecture | Ubuntu 24.04.5 LTS noble, kernel 6.8.0-62-generic, x86_64 |
| CPU/RAM/disk | 2 CPUs; RAM 3.8 GiB, available 3.2 GiB; root disk 24 GB, free 19 GB; swap 4 GiB |
| Existing listeners | Operator screenshot: SSH 22; loopback DNS 53 and X11 6010; no web listeners shown |
| Deploy user and effective Docker group | UID/GID 1000; docker group 988, verified in fresh SSH session |
| Docker Engine / Compose versions; Docker service | Engine 29.8.2; Compose v5.6.0; Docker active; deploy daemon access verified |
| Deployment public key and SHA256 fingerprint | Recorded below |
| VPS host public key SHA256 fingerprint | Recorded below, matched trusted operator root-session screenshots |
| Deploy SSH using pinned host key | PASS: BatchMode, IdentitiesOnly, StrictHostKeyChecking=yes |
| `/opt/nexora` and reviewed Compose/script | deploy:deploy mode 700 directory; executable deploy.sh 700, Compose 600 |
| `/opt/nexora/.env.production` | Operator populated; deploy:deploy mode 600 verified; required keys SET |
| `/root/nexora-secret-backup` and README | Not created; mode not verified |
| Private recovery files | Local dedicated SSH key outside git with restricted ACL; root-only VPS backup pending |
| GHCR package visibility / exact SHA image existence | Not verified |
| GHCR VPS pull authentication | Not configured/tested |
| Exact deployed main SHA | None |
| Container health / restart count / runtime non-root user | Not tested |
| API process / Worker process | Not started or verified |
| `/health/live` / DB readiness `/api/v1/health` | Not tested |
| Migrations / intended Neon production-v2 identity | Not run/verified |
| Production Swagger 404 | Not tested on VPS |
| Provider startup validation | Not tested on VPS |
| Render Worker database overlap | Operator confirms Render Worker is running on the same production-v2 DB; BLOCKS VPS startup |
| DNS cutover | **NOT PERFORMED** |
| Render changes | **NONE** |

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

Intended image reference for the main inspected in this record:
`ghcr.io/qbao0111/nexora-backend:d7b24a6852fea3aeed91eb8691bbe3faeab2b755`.
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
operator still needs to arrange the root-only recovery copy; deploy has no sudo and
no additional root access or privilege changes were introduced to work around that.

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
   chat/PR. The file now exists with empty credentials and mode `600`; do not print
   its contents after populating it.
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

## Recovery backup policy (VPS copy pending)

The deployment key has been generated outside git. Retain the requested emergency
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
on the VPS. No application startup, production migration, DNS record or Render
service was changed. Existing exact-main Backend
CI success is recorded above, not claimed as VPS health evidence.
