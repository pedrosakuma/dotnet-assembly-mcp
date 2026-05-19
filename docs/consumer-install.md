# Consumer install guide

`dotnet-assembly-mcp` ships in four shapes; pick the one that matches your topology.

| Shape | Best for | Transport | Port |
|---|---|---|---|
| **dotnet tool** | Single-developer laptops, local MCP clients (Claude Desktop, Cursor, VS Code, Copilot CLI) | stdio (default) **or** HTTP | 8788 (HTTP) |
| **Single-file binary** | Air-gapped or no-dotnet-runtime hosts | stdio **or** HTTP | 8788 (HTTP) |
| **Docker image** | Sidecar deployments, multi-client, CI runners | HTTP only | 8788 (host) → 8080 (container) |
| **Kubernetes** | Centralised in-cluster resolver | HTTP only | 8788 |

The conventional port split with [`dotnet-diagnostics-mcp`](https://github.com/pedrosakuma/dotnet-diagnostics-mcp):

- **diagnostics → `127.0.0.1:8787`**
- **assembly → `127.0.0.1:8788`**

All supervisor templates and the Docker image default to those ports. Override via `ASPNETCORE_URLS` or `--url=` when needed.

---

## 1. `dotnet tool` (recommended for local development)

Prerequisite: the **.NET 10 runtime** (`dotnet --list-runtimes` should show `Microsoft.NETCore.App 10.x`).

```bash
dotnet tool install -g dotnet-assembly-mcp
```

### Stdio (one MCP client process at a time)

Add to your MCP client's `mcp.json` (Claude Desktop: `~/Library/Application Support/Claude/claude_desktop_config.json`):

```jsonc
{
  "mcpServers": {
    "dotnet-assembly-mcp": {
      "command": "dotnet-assembly-mcp",
      "args": ["--stdio"]
    }
  }
}
```

### HTTP (multiple MCP clients sharing the same module cache)

Run the server in HTTP mode under a supervisor (see [§ 4](#4-supervisors)), then point each MCP client at the URL:

```jsonc
{
  "mcpServers": {
    "dotnet-assembly-mcp": {
      "url": "http://127.0.0.1:8788/mcp"
    }
  }
}
```

---

## 2. Single-file binary

GitHub releases publish self-contained, single-file binaries for every supported OS/arch:

| RID | Archive | sha256 |
|---|---|---|
| `linux-x64` | `dotnet-assembly-mcp-<ver>-linux-x64.tar.gz` | `.sha256` sibling |
| `linux-arm64` | `dotnet-assembly-mcp-<ver>-linux-arm64.tar.gz` | `.sha256` sibling |
| `win-x64` | `dotnet-assembly-mcp-<ver>-win-x64.zip` | `.sha256` sibling |
| `win-arm64` | `dotnet-assembly-mcp-<ver>-win-arm64.zip` | `.sha256` sibling |
| `osx-arm64` | `dotnet-assembly-mcp-<ver>-osx-arm64.tar.gz` | `.sha256` sibling |

Install (Linux/macOS example):

```bash
VER=0.7.1
RID=linux-x64
curl -fL -o assembly-mcp.tgz \
  "https://github.com/pedrosakuma/dotnet-assembly-mcp/releases/download/v${VER}/dotnet-assembly-mcp-${VER}-${RID}.tar.gz"
curl -fL -o assembly-mcp.tgz.sha256 \
  "https://github.com/pedrosakuma/dotnet-assembly-mcp/releases/download/v${VER}/dotnet-assembly-mcp-${VER}-${RID}.tar.gz.sha256"
sha256sum -c assembly-mcp.tgz.sha256
mkdir -p ~/.local/bin/dotnet-assembly-mcp
tar -xzf assembly-mcp.tgz -C ~/.local/bin/dotnet-assembly-mcp
ln -sf ~/.local/bin/dotnet-assembly-mcp/DotnetAssemblyMcp.Server ~/.local/bin/dotnet-assembly-mcp-bin
```

No .NET runtime required — the binary is self-contained. Use `--stdio` or HTTP exactly as with the `dotnet tool`.

---

## 3. Docker (HTTP / sidecar)

```bash
docker run --rm -d \
  --name dotnet-assembly-mcp \
  --restart unless-stopped \
  -p 127.0.0.1:8788:8080 \
  -v /path/to/assemblies:/assemblies:ro \
  -v dotnet-assembly-mcp-cache:/home/assemblymcp/.cache/dotnet-assembly-mcp \
  ghcr.io/pedrosakuma/dotnet-assembly-mcp:latest
```

- The image listens on `0.0.0.0:8080` inside the container; map to `127.0.0.1:8788` on the host to match the convention.
- A named volume (`dotnet-assembly-mcp-cache`) persists the `find_callers` xref index across restarts.
- HEALTHCHECK is baked in (`wget /health`).

Verify: `curl -fsS http://127.0.0.1:8788/health`.

---

## 4. Supervisors

Templates live under [`deploy/supervisors/`](../deploy/supervisors/). All three default to **`http://127.0.0.1:8788`** and gate readiness with `dotnet-assembly-mcp --health-check`.

### Linux — `systemd --user`

```bash
mkdir -p ~/.config/systemd/user
cp deploy/supervisors/linux/dotnet-assembly-mcp.service ~/.config/systemd/user/
loginctl enable-linger "$USER"
systemctl --user daemon-reload
systemctl --user enable --now dotnet-assembly-mcp.service
```

Logs: `journalctl --user -u dotnet-assembly-mcp.service -f`.

### Windows — Scheduled Task

```powershell
powershell -ExecutionPolicy Bypass -File deploy\supervisors\windows\Install-Service.ps1
```

The script registers a per-user Scheduled Task (no admin), pins `ASPNETCORE_URLS=http://127.0.0.1:8788`, redirects stderr to `%LOCALAPPDATA%\dotnet-assembly-mcp\logs\server.stderr.log`, and runs the `--health-check` probe before declaring success.

Uninstall:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\supervisors\windows\Install-Service.ps1 -Action Uninstall
```

### macOS — `launchd`

```bash
# Substitute REPLACE_ME with your actual /Users/<you> path before bootstrapping.
sed "s|/Users/REPLACE_ME|$HOME|g" \
    deploy/supervisors/macos/io.github.pedrosakuma.dotnet-assembly-mcp.plist \
    > ~/Library/LaunchAgents/io.github.pedrosakuma.dotnet-assembly-mcp.plist
mkdir -p ~/Library/Logs/dotnet-assembly-mcp
launchctl bootstrap gui/"$(id -u)" \
    ~/Library/LaunchAgents/io.github.pedrosakuma.dotnet-assembly-mcp.plist
```

Logs: `tail -F ~/Library/Logs/dotnet-assembly-mcp/server.{out,err}.log`.

---

## 5. Kubernetes

See [`deploy/k8s/`](../deploy/k8s/) for the full Deployment + Service manifest. One pod per cluster (this server is not a sidecar), assemblies mounted read-only via PVC, xref cache on a persistent volume.

```bash
kubectl apply -f deploy/k8s/sample.yaml
kubectl -n assemblymcp port-forward svc/dotnet-assembly-mcp 8788:8788
```

---

## 6. Sanity check

Regardless of the install shape:

```bash
# Exec probe (works for stdio installs too)
dotnet-assembly-mcp --health-check --url=http://127.0.0.1:8788/health
echo "exit code: $?"   # 0 = healthy

# HTTP probe
curl -fsS http://127.0.0.1:8788/health
```

If `--health-check` returns 0 and `curl /health` returns `{ "status": "ok" }`, every install path is wired correctly.

---

## 7. Pairing with `dotnet-diagnostics-mcp`

`dotnet-assembly-mcp` exists to **resolve** the `MethodIdentity` / `TypeIdentity` records that `dotnet-diagnostics-mcp` emits from running .NET processes. The handoff is documented in [`docs/handoff-contract.md`](./handoff-contract.md).

Recommended companion install order:

1. Install **both** servers using their respective consumer-install guides.
2. Use the conventional ports: `127.0.0.1:8787` (diagnostics) and `127.0.0.1:8788` (assembly).
3. Wire both into the same MCP client config.
4. Diagnostic captures will now drill into typed methods automatically.

A consumer who installs only this server gets a fully working **static** navigator; they just don't get live-process hotspots until `dotnet-diagnostics-mcp` is also installed.
