# ComposeToNginx

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A .NET CLI that turns a Docker Compose file into proxy hosts in [NGINX Proxy Manager (NPM)](https://github.com/NginxProxyManager/nginx-proxy-manager). Each service with a published port becomes a host you can review and push — no web UI clicking required.

Declare proxy intent declaratively with `npm.*` labels for fully non-interactive CI/CD pipelines, or use the interactive prompts for ad-hoc setup. Already managing hosts in NPM? `hosts pull` backfills the matching labels onto your Compose file automatically.

## Requirements

- .NET 10 SDK
- A reachable NGINX Proxy Manager instance (default `http://127.0.0.1:81`)

## Build

The shared `Shiron.Lib` is pulled in as a git submodule:

```bash
git clone --recurse-submodules <repo-url>
# or, for an existing clone:
git submodule update --init
```

```bash
dotnet build --configuration Release
```

The output binary is the `compose-to-nginx` executable in `src/Cli/bin/Release/net10.0/`.

> The NPM API client under `src/generated/NginxProxySdk` is generated with [Kiota](https://learn.microsoft.com/openapi/kiota/) from a live NPM schema. Regenerate with `bash scripts/generate-sdk.sh` (requires NPM running locally). You normally don't need to do this.

## Configuration

Every command authenticates against NPM. Provide credentials via flags or environment variables — a local `.env` is auto-loaded.

| Setting | Flag | Env var | Required | Default |
|---|---|---|---|---|
| NPM URL | `--host` | `NPM_HOST` | no | `http://127.0.0.1:81` |
| Email | `--email` | `NPM_EMAIL` | yes | — |
| Password | `--password` | `NPM_PASSWORD` | yes | — |

Example `.env`:

```dotenv
NPM_HOST=http://127.0.0.1:81
NPM_EMAIL=admin@example.com
NPM_PASSWORD=changeme
```

Flags override environment variables, which override defaults.

## Usage

```
compose-to-nginx <command> [options]
```

### Shared flags

Commands that **write** state (`hosts push`, `hosts add`, `hosts pull`) share these transactional flags:

| Flag | Short | Description |
|---|---|---|
| `--dry-run` | | Accumulate and preview the planned changes without applying them. Fully non-interactive. |
| `--yes` | `-y` | Skip the final confirmation prompt. Auto-enabled when the `CI` environment variable is set and stdout is not a TTY. |

**Transactional model.** Every mutating command follows the same flow:

1. **Accumulate** — gather every change (resolve certificates, detect conflicts, build request bodies). If anything fails, the command **aborts** without touching NPM.
2. **Present** — render a summary of the planned changes.
3. **Confirm** — ask for explicit approval (skipped with `--yes` or `--dry-run`).
4. **Apply** — execute the changes against NPM (skipped under `--dry-run`).

### `hosts push <file>`

Reads a Compose file and reconciles proxy hosts in NPM against it. Services with `npm.*` labels are planned automatically; unlabelled services are handled according to [`--label-mode`](#--label-mode). Hosts that already match NPM are left untouched (see [Sync detection](#sync-detection)).

```bash
# Preview only — nothing is written to NPM
compose-to-nginx hosts push docker-compose.yml --dry-run

# Create the hosts for real
compose-to-nginx hosts push docker-compose.yml

# Fully non-interactive (CI/CD) — labels required, no prompts
compose-to-nginx hosts push docker-compose.yml --yes
```

#### `--label-mode`

Controls how `npm.*` labels are interpreted:

| Mode | Behaviour |
|---|---|
| `auto` (default) | **All services labelled** → label-driven (no prompts). **Some labelled** → asks whether to skip the unlabelled services or fill them in interactively. **None labelled** → interactive mode for all. With `--yes`, unlabelled services are always skipped. |
| `require` | Every service with a port must have an `npm.host` label. The command **errors** if any are missing. |
| `ignore` | Ignore all labels; always use interactive prompts. |

```bash
compose-to-nginx hosts push docker-compose.yml --label-mode require --yes
```

#### Overwrite detection

- The **first published port** on each service becomes the forward port (unless overridden by `npm.forward-port`); services with no usable single numeric port are skipped.
- Existing NPM hosts with the same domain signature are detected and **overwritten** (deleted then recreated).
- When SSL is enabled, a certificate is resolved (see [Certificate resolution](#certificate-resolution)).

#### Sync detection

`hosts push` is idempotent. Before applying, each planned host is compared against the existing NPM host it would overwrite (forward host/port, SSL, certificate). Hosts that already match are **skipped**. If every host is already in sync, the command reports "nothing to do" and exits without making changes.

### `hosts pull <file>`

The inverse of `hosts push`: reads the proxy hosts already in NPM and **backfills `npm.*` labels** onto the matching services in a Compose file — turning a manually managed (or previously pushed) setup into a label-driven one.

Each service is matched to an NPM host by its published port (preferring a forward-host name match), and the minimal set of labels reproducing that host's configuration is derived. Services that already carry `npm.host`, expose no ports, or have no matching host are skipped.

```bash
# Preview the labels that would be added
compose-to-nginx hosts pull docker-compose.yml --dry-run

# Backfill in place (after review)
compose-to-nginx hosts pull docker-compose.yml

# Write to a separate file to review before replacing the original
compose-to-nginx hosts pull docker-compose.yml --output docker-compose.labelled.yml
```

The Compose file is edited surgically — comments, formatting, and non-`npm.*` labels are preserved, and the result is re-parsed to verify it's still valid YAML before being written.

| Flag | Description |
|---|---|
| `--output <file>` | Write the updated Compose file here instead of overwriting the input. |
| `--cert-ref <mode>` | How to write `npm.cert` for SSL hosts (see below). |

#### `--cert-ref`

Controls how the `npm.cert` label is written for SSL hosts. Prompted interactively when omitted (and at least one SSL host exists); defaults to `none` under `--yes`.

| Mode | Behaviour |
|---|---|
| `none` (default) | Omit `npm.cert`. On `push`, the certificate is inferred from the host domain (see [Certificate resolution](#certificate-resolution)). Produces the shortest labels. |
| `nice-name` | Write the certificate's NPM name verbatim. Explicit, and disambiguates when multiple certificates cover the same domain, but brittle if the certificate's domain order (and therefore its auto-generated name) ever changes. |

```bash
compose-to-nginx hosts pull docker-compose.yml --cert-ref nice-name
```

### `hosts ls`

List current NPM proxy hosts (ID, domains, forward target, SSL, status).

### `hosts add`

Interactively create a single proxy host (domains, scheme, forward host/port, certificate, SSL/HSTS/caching toggles). Supports `--dry-run` and `--yes` like `hosts push`.

### `certificates ls`

List NPM certificates with provider, domains, and expiry status.

## Label-driven configuration

Proxy-host intent can be declared entirely via Docker Compose labels, enabling fully non-interactive `hosts push` runs. A service **without** `npm.host` is ignored by default (no prompt).

### Example

```yaml
services:
  api:
    image: my-api:latest
    container_name: api
    ports: ["8080:80"]
    labels:
      npm.host: "api.example.com"
      npm.ssl: "true"
      npm.cert: "wildcard.example.com"

  web:
    image: nginx:latest
    ports: ["80:80"]
    labels:
      npm.host: "www.example.com,example.com"
```

### Label reference

| Label | Required | Default | Purpose |
|---|---|---|---|
| `npm.host` | yes (to enable) | — | Primary domain. Comma-separated for SANs. |
| `npm.alias.<n>` | no | — | Additional domains (alternative to comma syntax). E.g. `npm.alias.0=www.example.com`. |
| `npm.ssl` | no | `false` | Enable SSL. |
| `npm.cert` | no | auto-derive | Certificate to attach, by nice-name or domain. If omitted, a certificate covering `npm.host` is searched automatically. **If none is found, the command aborts with an error.** |
| `npm.force-ssl` | no | follows `npm.ssl` | Force HTTPS redirect. |
| `npm.http2` | no | follows `npm.ssl` | HTTP/2 support. |
| `npm.websocket` | no | `true` | Allow WebSocket upgrade. |
| `npm.block-exploits` | no | `true` | Block common exploits. |
| `npm.caching` | no | `false` | Enable caching. |
| `npm.hsts` | no | `false` | Enable HSTS. |
| `npm.scheme` | no | `http` | Forward scheme to backend (`http` or `https`). |
| `npm.enabled` | no | `true` | Host enabled flag. |
| `npm.forward-host` | no | container name / service name | Override forward host. |
| `npm.forward-port` | no | first published port | Override forward port (1–65535). |

Boolean labels accept: `true`/`false`, `1`/`0`, `yes`/`no`, `on`/`off` (case-insensitive).

### Certificate resolution

When `npm.ssl=true`, ComposeToNginx must attach a certificate. The resolution order is:

1. **`npm.cert` specified** — looks up the certificate by nice-name first, then by exact domain match, then by wildcard coverage. If not found, the command **aborts**.
2. **`npm.cert` omitted** — auto-derives from `npm.host`: searches all NPM certificates for one whose domains cover the host's primary domain (exact match preferred, then wildcard like `*.example.com`). If none is found, the command **aborts** with an error listing the affected service and domain.

This is a hard stop, not a warning — SSL cannot be partially applied.

## Non-interactive mode

`--yes` (or `-y`) skips the confirmation prompt before applying changes. It is **auto-enabled** when:

- The `CI` environment variable is set, **and**
- stdout is not a TTY (i.e. output is redirected/piped).

In non-interactive mode:

- Services with `npm.host` labels are planned from the labels — no prompts.
- Services **without** labels are **skipped with a warning** (never prompted).
- Any missing required value (e.g. unresolvable certificate) is a **hard error**, not a prompt.

```bash
# CI/CD pipeline — fully automated
compose-to-nginx hosts push docker-compose.yml --yes --label-mode require
```

## License

[MIT](LICENSE)
