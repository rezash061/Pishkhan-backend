# Pishkhan backend

AI-powered Instagram DM automation for Persian-speaking shops. This repository holds the .NET 10 backend; the plans it implements are in [docs/plans](docs/plans) (start with `00-overview.md` and `01-backend.md`).

## Layout

```
backend/
  src/BuildingBlocks/   SharedKernel, Contracts (events, topics), Persian, Infrastructure, Web, AI
  src/Modules/          Identity, Tenancy, Backoffice, Files, Channels, Conversations, Agent (+ *.Contracts each)
  src/Hosts/            Api, AdminApi, Ingress, Workers, Migrator (composition only)
  tools/                Pishkhan.EvalRunner, smoke/ (M0 end-to-end test with a mock Meta API)
  tests/                Persian, Unit, Architecture tests
  config/               settings shared by every host (appsettings.json, appsettings.Development.json)
  infra/                docker-compose.yml for local Postgres, two Redis, Kafka, ClamAV
  openapi/              exported contracts: api.v1.json, public.v1.json, admin.v1.json
```

Modular monolith: each module owns one PostgreSQL schema and talks to other modules only through their `*.Contracts` project (in-process interfaces) or Kafka events written through a transactional outbox. The architecture tests enforce this.

## Run locally

Requirements: .NET SDK 10, Docker.

```bash
cd backend
docker compose -f infra/docker-compose.yml up -d
DOTNET_ENVIRONMENT=Development dotnet run --project src/Hosts/Pishkhan.Migrator -- migrate   # schemas, partitions, Quartz tables, topics
dotnet run --project src/Hosts/Pishkhan.Api        # http://localhost:5080  (OpenAPI: /openapi/api.json)
dotnet run --project src/Hosts/Pishkhan.Ingress    # http://localhost:5090  (Meta webhooks)
dotnet run --project src/Hosts/Pishkhan.Workers    # http://localhost:5070  (all worker groups in dev)
dotnet run --project src/Hosts/Pishkhan.AdminApi   # http://localhost:5081
```

In Development, OTP codes are not sent by SMS: read them from `GET /dev/sms`. Dev JWT keys are generated under `~/.pishkhan/keys`.
First staff account: `dotnet run --project src/Hosts/Pishkhan.Migrator -- create-staff <username> <password> super_admin`.

Instagram needs a Meta app (development mode). Put its values in user secrets, never in files:

```bash
dotnet user-secrets --id pishkhan-dev set Instagram:AppId <id>
dotnet user-secrets --id pishkhan-dev set Instagram:AppSecret <secret>
dotnet user-secrets --id pishkhan-dev set Instagram:WebhookVerifyToken <any random string>
```

## Tests

```bash
dotnet test --project tests/Pishkhan.Persian.Tests
dotnet test --project tests/Pishkhan.UnitTests
dotnet test --project tests/Pishkhan.ArchitectureTests
```

### Smoke test (milestone M0 without a real Meta app)

Start `tools/smoke/mock_meta.py`, start Api, Ingress and Workers with `Instagram__TokenUrl=http://localhost:5999/oauth/access_token`, `Instagram__GraphBaseUrl=http://localhost:5999`, `Instagram__AppSecret=test-app-secret`, `Instagram__WebhookVerifyToken=verify-me` and any `Instagram__AppId`, then run `python3 tools/smoke/m0_smoke.py`. It covers OTP login, workspace creation, Instagram OAuth connect, webhook verification and signature check, DM storage, the echo reply and cross-tenant isolation.

## Evals

```bash
dotnet run --project tools/Pishkhan.EvalRunner -- import smoke tools/Pishkhan.EvalRunner/samples/smoke.jsonl
dotnet run --project tools/Pishkhan.EvalRunner -- run smoke
```

The model used for each task comes from `Ai:Routes` in configuration (a fallback chain per task), so switching models needs no code change.
