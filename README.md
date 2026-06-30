# ComposeToNginx

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A .NET CLI that turns a Docker Compose file into proxy hosts in [NGINX Proxy Manager (NPM)](https://github.com/NginxProxyManager/nginx-proxy-manager). Each service with a published port becomes a host you can review and push — no web UI clicking required.

Declare proxy intent declaratively with `npm.*` labels for fully non-interactive CI/CD pipelines, or use the interactive prompts for ad-hoc setup.

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

Commands that **mutate** NPM state (`hosts push`, `hosts add`) share these transactional flags:

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

Reads a Compose file and creates one proxy host per service. Two modes:

- **Label-driven** (non-interactive): services with `npm.host` labels are planned automatically.
- **Interactive** (fallback): services without labels prompt for a domain choice.

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
| `auto` (default) | Use labels when present; fall back to interactive prompts for unlabelled services. In `--yes` mode, unlabelled services are **skipped with a warning**. |
| `require` | Every service with a port must have an `npm.host` label. The command **errors** if any are missing. |
| `ignore` | Ignore all labels; always use interactive prompts. |

```bash
compose-to-nginx hosts push docker-compose.yml --label-mode require --yes
```

#### Overwrite detection

- The **first published port** on each service becomes the forward port (unless overridden by `npm.forward-port`); services with no usable single numeric port are skipped.
- Existing NPM hosts with the same domain signature are detected and **overwritten** (deleted then recreated).
- When SSL is enabled, a certificate is resolved (see [Certificate resolution](#certificate-resolution)).

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
