# ComposeToNginx

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A .NET CLI that turns a Docker Compose file into proxy hosts in [NGINX Proxy Manager (NPM)](https://github.com/NginxProxyManager/nginx-proxy-manager). Each service with a published port becomes a host you can review and push — no web UI clicking required.

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

### `hosts push <file>`

The headline command. Reads a Compose file and proposes one proxy host per service that exposes a port. For each service you pick **include** (as `<service>.<base-domain>`), **custom domain**, or **skip**.

```bash
# Preview only — nothing is written to NPM
compose-to-nginx hosts push docker-compose.yml --dry-run

# Create the hosts for real
compose-to-nginx hosts push docker-compose.yml
```

Behavior:

- The **first published port** on each service becomes the forward port; services with no usable single numeric port are skipped.
- You're prompted for a default forward host (default `127.0.0.1`) and a base domain.
- Existing NPM hosts with the same domain signature are detected and **overwritten** (deleted then recreated).
- SSL is optional; when enabled, you select an existing NPM certificate. NPM terminates TLS and forwards plain HTTP.

### `hosts ls`

List current NPM proxy hosts (ID, domains, forward target, SSL, status).

### `hosts add`

Interactively create a single proxy host (domains, scheme, forward host/port, certificate, SSL/HSTS/caching toggles).

### `certificates ls`

List NPM certificates with provider, domains, and expiry status.

## License

[MIT](LICENSE)
