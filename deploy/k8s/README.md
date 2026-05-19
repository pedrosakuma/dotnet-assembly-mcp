# Deploying dotnet-assembly-mcp on Kubernetes

This folder contains a working Deployment + Service for running
`dotnet-assembly-mcp` as a centralised resolver that any in-cluster MCP client
(including `dotnet-diagnostics-mcp` sidecars) can call to translate
`(moduleVersionId, metadataToken)` handoff payloads back to typed methods.

## Files

- [`sample.yaml`](./sample.yaml) — Namespace, ConfigMap (assembly roots), Deployment
  with the `ghcr.io/pedrosakuma/dotnet-assembly-mcp` image, a `PersistentVolumeClaim`
  for the xref disk cache, and a `ClusterIP` Service that exposes the MCP endpoint
  on port `8788`.

## How this differs from `dotnet-diagnostics-mcp`

`dotnet-diagnostics-mcp` runs **per-pod as a sidecar** because it needs to share
the target process's PID namespace and `/tmp` socket. This server does **not**:
it reads `.dll` files from disk and never touches a running process. The natural
shape is therefore:

- **One Deployment** of `dotnet-assembly-mcp` per cluster (or per team).
- A read-only volume mount of the directory tree that holds the assemblies you
  want to resolve.
- A persistent volume for `~/.cache/dotnet-assembly-mcp` so the xref cache
  survives pod restarts (rebuilding it is O(modules × MethodDef rows)).

If you only need the resolver during a debugging window, scale the Deployment
to zero between sessions — the disk cache rehydrates automatically.

## Assembly source

The sample mounts `/assemblies` from a `PersistentVolumeClaim`. Populate it
however fits your build pipeline:

1. CI publishes images plus a tarball of the corresponding assemblies to S3,
   and an `initContainer` rsyncs them into the PVC at pod start.
2. A separate Deployment runs `kubectl cp` from your registry's debug image.
3. Mount your existing build cache directly if the cluster has access to it.

The server's `import_assembly_manifest` tool also accepts arbitrary paths if
you'd rather push assemblies on demand from an MCP client.

## Auth

Static bearer token, opt-in. Set `ASSEMBLY_MCP_BEARER_TOKEN` (or
`MCP_BEARER_TOKEN` for the cross-server fallback shared with
`dotnet-diagnostics-mcp`) on the server. When neither is set the HTTP
transport accepts all requests — that's the back-compat path for local
`127.0.0.1` deploys; **always set a token in any shared/clustered
topology**.

When configured, every request to `/mcp` (and any other route) must
carry `Authorization: Bearer <token>`. `/health` is always exempt so
liveness/readiness probes don't need the secret. Comparison is
fixed-time (`CryptographicOperations.FixedTimeEquals`). Mismatch or
missing header returns `401 Unauthorized` with `WWW-Authenticate:
Bearer`.

Out of scope here: OAuth/OIDC (overkill for tooling), mTLS (handle at
the ingress), per-tool ACLs (one global token).

Example manifest snippet:

```yaml
env:
  - name: ASSEMBLY_MCP_BEARER_TOKEN
    valueFrom:
      secretKeyRef: { name: dotnet-assembly-mcp-auth, key: token }
```

```bash
kubectl -n assemblymcp create secret generic dotnet-assembly-mcp-auth \
  --from-literal=token="$(openssl rand -hex 32)"
```

Defence in depth still recommended:

- Keep the Service `ClusterIP` (no Ingress / LoadBalancer).
- Use a NetworkPolicy to restrict ingress to the consuming namespaces.

## Verification

```bash
kubectl apply -f deploy/k8s/sample.yaml

# Health
kubectl -n assemblymcp port-forward svc/dotnet-assembly-mcp 8788:8788 &
curl -fsS http://127.0.0.1:8788/health

# MCP handshake (from any pod with curl)
kubectl -n assemblymcp run --rm -it --image=curlimages/curl mcp-test -- \
  curl -X POST http://dotnet-assembly-mcp:8788/mcp \
       -H 'Content-Type: application/json' \
       -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{...}}'
```
