# Deployment notes

Notes on how the hosts are exposed. Hosts and health checks are listed in [runbooks/README.md](runbooks/README.md).

## Inbox hub (`/hubs/inbox`) is served from the panel's own origin

The `Api` host has **no CORS policy**, for the REST API or for SignalR. The browser never calls the `Api` host cross-origin. REST goes
through the panel's `/api/bff/*` proxy, and the inbox hub must also be reachable at `/hubs/inbox` **on the panel's own origin**
(`app.example.ir/hubs/inbox`), forwarded to the `Api` host. Do not add a CORS policy to make a direct cross-origin connection work.
Same origin keeps the hub out of reach of other sites and needs no preflight.

How the hub authenticates:

- The panel gets a 5-minute hub-scoped token from `POST /api/v1/auth/realtime-token` (through its BFF) and passes it as
  `accessTokenFactory`. SignalR sends it as the `access_token` query parameter on the WebSocket, SSE and long-polling requests. Only paths
  under `/hubs` accept a token in the query string, and only the realtime scheme accepts it.
- The token is short-lived, but it is still a credential. **Strip `access_token` from proxy access logs** (the API's own logs already
  redact it).

### Production (Nginx in front of the panel)

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }

# Log the path without its query string, so access_token never reaches the access log.
log_format panel '$remote_addr [$time_local] "$request_method $uri" $status $body_bytes_sent $request_time';

server {
    server_name app.example.ir;
    access_log /var/log/nginx/panel.log panel;

    location /hubs/ {
        proxy_pass http://api:8080;            # the Api host, not the Next.js app
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;  # WebSockets
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_buffering off;                    # server-sent events fallback
        proxy_read_timeout 120s;                # well above the 15 s keep-alive and 40 s client timeout
    }

    location / {
        proxy_pass http://user-panel:3000;      # Next.js, including /api/bff/*
        # ...
    }
}
```

With more than one `Api` replica, SignalR needs either sticky sessions or WebSockets only (`skipNegotiation: true` with
`transport: WebSockets` in the client). Hub groups are shared across replicas through the Redis backplane.

### Development (Next.js rewrite)

Add a rewrite in the panel's `next.config` from `/hubs/:path*` to `${API_URL}/hubs/:path*`. The Next dev server forwards negotiate and
long-polling requests through rewrites, but it may not upgrade WebSockets, in which case SignalR falls back to SSE or long polling. That is
fine for development. To test WebSockets locally, run the same Nginx block (or any reverse proxy) in front of `next dev`.

### Admin panel and website

The admin panel uses no hub. The website has no realtime connection. Both call their backends only through their own BFF routes.

## Kubernetes (BE-2.11)

From the public launch the hosts run on Kubernetes with the Helm chart in `backend/deploy/helm/pishkhan`. PostgreSQL (with PgBouncer),
Kafka, both Redis instances, MinIO, ClamAV and the OpenTelemetry collector are outside the chart; `values.yaml` points at them.

- **Images.** One per host, built by the SDK (`dotnet publish -c Release -t:PublishContainer`, see `src/Hosts/Directory.Build.props`) on
  Ubuntu chiseled with ICU and tzdata (`noble-chiseled-extra`): non-root (`app`, uid 1654), no shell, port 8080. The `deploy-artifacts`
  workflow builds them on every pull request and pushes them to Harbor from `main` (secrets `REGISTRY`, `REGISTRY_USER`,
  `REGISTRY_PASSWORD`). See ADR 0014.
- **Releases.** `helm upgrade --install pishkhan backend/deploy/helm/pishkhan -f values-production.yaml --set image.tag=<sha>`. The
  Migrator runs first as a `pre-install,pre-upgrade` hook and the release stops if it fails. Migrations stay backward compatible with the
  running release (expand now, contract in a later release), so the rolling update never runs old code on a schema it does not know.
- **Workloads.** `api`, `adminapi` and `ingress` Deployments with Services and Ingresses (the admin API behind an IP allowlist), and one
  `workers-<group>` Deployment per consumer group (`Workers__Groups__0`). Each has an HPA on CPU (fast up, slow down), a PDB of one,
  spread over nodes and zones, probes on `/health/live` and `/health/ready`, a read-only root filesystem and a 45-second grace period so
  Kafka consumers commit before they stop.
- **Secrets.** The chart references one existing Secret (`existingSecret`), created from Vault. Nothing secret is in values files.
- **The inbox hub.** The panel's origin still forwards `/hubs/` to the Api Service (section above); with more than one Api replica keep
  the client on WebSockets only, or turn on sticky sessions in the ingress controller.
- **Network policies.** HTTP hosts accept traffic only from the ingress controller's namespace and the release's own namespace; worker
  pods accept nothing from outside the namespace.

Disaster recovery for the cluster and the data: [runbooks/disaster-recovery.md](runbooks/disaster-recovery.md). Launch gates:
[launch-m2.md](launch-m2.md).
