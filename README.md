# Document Management System (DMS)

An event-driven document management platform: upload documents, convert them to PDF / PDF/A, archive them, classify and extract structured data, index them for search, and review/edit them as versioned revisions. Access is protected with Microsoft Entra ID (OIDC + role-based access control).

## Features

- **Upload & storage** – authenticated upload, list and download of documents (PostgreSQL metadata + file storage keyed by document GUID).
- **Office → PDF conversion** – LibreOffice (container) converts Office documents to PDF.
- **PDF/A conversion & validation** – Ghostscript (container) produces PDF/A-2b; veraPDF (REST) validates compliance.
- **Revisions** – every edit is saved as a new revision; list, download and restore earlier versions.
- **Classification & orchestration** – `OrchestratorService` classifies documents (invoice, contract, identity, financial statement, …) and routes them.
- **Extraction** – `ExtractionService` uses Azure Document Intelligence plus an LLM agent (Foundry Local or Microsoft Foundry) with a critic loop and validators (e.g. invoice validation).
- **Search** – `SearchService` chunks, embeds and indexes content; deterministic, hybrid and agentic search endpoints.
- **RBAC** – roles Viewer / Contributor / Reviewer / Admin enforced by the API and reflected in the UI.
- **Reliable messaging** – transactional outbox in each service, RabbitMQ with dead-letter queues.

## Architecture

```mermaid
flowchart LR
  UI[Angular client :4200] -- Bearer token --> API[DocuManagementApp API :5176]
  API --> PG[(documanagementdb)]
  API --> CONV[LibreOffice / Ghostscript containers]
  API --> VERA[veraPDF REST :8080]
  API -- outbox --> MQ{{RabbitMQ documents.events}}
  MQ --> ORC[OrchestratorService]
  ORC -- extraction request --> MQ2{{extraction queue}}
  MQ2 --> EXT[ExtractionService :5085 artifact API]
  EXT --> DI[Document Intelligence :5000]
  EXT --> LLM[Foundry Local / Microsoft Foundry]
  EXT -- index request --> MQ3{{search queue}}
  MQ3 --> SRCH[SearchService :5190]
  SRCH --> EXT
  API -- proxy --> SRCH
```

### Pipeline

1. **Upload** – `POST /api/fileupload/upload` stores the file; `DocumentIngestionService` writes the record and an outbox message in one transaction.
2. **Conversion** – Office files go through LibreOffice (`OfficeToPdfConversionService`); PDFs can be converted to PDF/A with Ghostscript and validated with veraPDF (`GhostscriptVeraPdfAProcessingService`, `/api/pdfa/*`).
3. **Archiving/storage** – `DocumentStorageService` stores files under the document GUID; revisions are kept in `DocumentRevisions`.
4. **Publish** – `OutboxPublisher` publishes `document.ingested` to exchange `documents.events` (publisher confirms, retry with backoff).
5. **Orchestrate** – `OrchestratorService` consumes `orchestrator.document-ingested`, classifies by file name/content type (`DocumentClassifier`) and emits extraction requests via its own outbox.
6. **Extract** – `ExtractionService` fetches the document from the API (`DocumentContentApi`), runs layout extraction, optional model extraction, the critic loop and validators, stores artifacts and serves them on its artifact API.
7. **Index & search** – `SearchService` reads artifacts, chunks (`SearchChunker`), creates embeddings and stores them in `searchdb`.

### Backend projects

| Project | Role | Default URL | Database |
|---|---|---|---|
| `DocuManagementApp` | REST API, conversion, storage, revisions, auth, search proxy | http://localhost:5176 | `documanagementdb` |
| `OrchestratorService` | Consumes ingest events, classifies, routes | – (worker) | `orchestratordb` |
| `ExtractionService` | Extraction agent + artifact API | http://localhost:5085 | `extractiondb` |
| `SearchService` | Chunking, embeddings, search API | http://localhost:5190 | `searchdb` |
| `DocuManagementApp.Tests` | Unit tests | – | – |

### API overview

| Endpoint | Policy |
|---|---|
| `POST /api/fileupload/upload`, `GET /api/fileupload/files`, `GET /api/fileupload/download/{id}`, `POST /api/fileupload/{id}/convert-to-pdf` | authenticated |
| `/api/pdfa/generate \| convert \| validate \| suggestions \| last-report` | authenticated |
| `GET /api/documents/{id}/revisions`, `GET …/{n}/content` | `DmsRead` |
| `POST /api/documents/{id}/revisions` | `DmsWrite` |
| `POST …/{n}/restore` | `DmsReview` |
| `POST /api/search/deterministic \| hybrid \| agentic` | authenticated |

### Frontend (`angClientapp`)

Angular 21 (standalone components, Vitest), MSAL (`@azure/msal-angular`). An MSAL interceptor attaches the bearer token to `http://localhost:5176/api/*` (`protectedResourceMap`). Features: file upload and list (with "Verlauf / Bearbeiten" link), revision history/editor, search. Authenticated downloads are fetched as blobs, because a plain `<a href>` cannot send a bearer token.

### Authentication & RBAC

- API: Microsoft.Identity.Web JWT bearer, `MapInboundClaims=false`, `RoleClaimType="roles"`.
- Policies: `DmsRead`, `DmsWrite`, `DmsReview`, `DmsAdmin`.
- Entra app registration (`DocumentManagementApp`): identifier URI `api://<ClientId>`, scope `access_as_user`, SPA redirect URI `http://localhost:4200/`, app roles `Viewer`, `Contributor`, `Reviewer`, `Admin` (assign users in *Enterprise applications → Users and groups*).
- Client scope: `api://<ClientId>/access_as_user`. Do **not** set `AzureAd:Audience` on the API.

## Prerequisites

- .NET 9 SDK, `dotnet-ef` tool (`dotnet tool install -g dotnet-ef`)
- Node.js LTS and Angular CLI (`npm i -g @angular/cli`)
- Docker or Podman
- PostgreSQL 16+ (pgvector recommended for embeddings)
- RabbitMQ (provided by `compose.yaml`)
- veraPDF REST service on `:8080`
- Optional: Foundry Local / Microsoft Foundry, Azure Document Intelligence (or a compatible container on `:5000`)

## Configuration

Keep secrets out of source control; use user-secrets or environment variables (`ConnectionStrings__DefaultConnection`, `RabbitMq__UserName`, …).

| Service | Key settings |
|---|---|
| API | `AzureAd` (TenantId, ClientId), `ConnectionStrings:DefaultConnection`, `PdfA` (container runtime, `GhostscriptImage`, `VeraPdfRestBaseUrl`), `LibreOffice`, `RabbitMq` (host, exchange `documents.events`, outbox tuning, UserName/Password), `SearchProxy` (Enabled, BaseUrl, ApiKey) |
| Orchestrator | `ConnectionStrings:Orchestrator`, `RabbitMq` (queues, dead-letter, credentials) |
| Extraction | `ConnectionStrings:Extraction`, `ExtractionArtifactApi`, `DocumentContentApi:BaseUrl`, `DocumentIntelligence`, `Foundry`, `ExtractionProcessing`, `RabbitMq` |
| Search | `ConnectionStrings:Search`, `Search` (ListenUrl, ApiKey, TenantId, SourceDocumentBaseUrl), `ArtifactApi:BaseUrl`, `Embeddings`, `AgenticSearch`, `Search:RabbitMq` |

Example:

```powershell
cd DocuManagementApp
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=documanagementdb;Username=postgres;Password=<pw>"
dotnet user-secrets set "RabbitMq:UserName" "dms"
dotnet user-secrets set "RabbitMq:Password" "<pw>"
```

Angular: set the tenant, client ID, API scope and API base URL in the MSAL configuration under `angClientapp/src/app`.

## Setup & run

1. **RabbitMQ**
   ```powershell
   $env:RABBITMQ_DEFAULT_USER="dms"; $env:RABBITMQ_DEFAULT_PASS="<pw>"
   docker compose up -d rabbitmq      # AMQP 5672, management UI http://localhost:15672
   ```
2. **PostgreSQL** – start it and create `documanagementdb`, `orchestratordb`, `extractiondb`, `searchdb`.
3. **veraPDF REST** – run a veraPDF REST container publishing port 8080. Pull the Ghostscript and LibreOffice images once (`docker pull docker.io/minidocks/ghostscript:latest docker.io/linuxserver/libreoffice:latest`).
4. **Migrations** (API; workers create/migrate their schema on start-up as configured)
   ```powershell
   cd DocuManagementApp; dotnet ef database update
   ```
5. **Start services** (one terminal each; start order: API → Orchestrator → Extraction → Search)
   ```powershell
   dotnet run --project DocuManagementApp        # :5176
   dotnet run --project OrchestratorService
   dotnet run --project ExtractionService        # :5085
   dotnet run --project SearchService            # :5190
   ```
   To enable search from the UI set `SearchProxy:Enabled=true` in the API.
6. **Frontend**
   ```powershell
   cd angClientapp; npm install; ng serve        # http://localhost:4200
   ```

### Keeping it running

- RabbitMQ: `restart: unless-stopped` in compose (or `docker compose up -d`) keeps the broker and its `rabbitmq-data` volume alive across reboots.
- Services: use `dotnet watch run` during development, or publish (`dotnet publish -c Release`) and run as Windows Services / systemd units / containers with automatic restart. Messages are durable and outboxes retry, so workers can be restarted without data loss.
- VS Code `.vscode/launch.json` contains "Attach to …" configurations for debugging.
- Stop a running API before `dotnet build` (the running exe is locked).

## Tests

```powershell
dotnet test DocuManagementApp.Tests
cd angClientapp; ng test
```

## Troubleshooting

- **401** – no bearer token sent: check `protectedResourceMap` and that the API scope is `api://<ClientId>/access_as_user`.
- **403** – user has no app role: assign a role in Entra, sign out/in; confirm the token's `roles` claim.
- **Revision endpoints fail** – run `dotnet ef database update`.
- **PDF/A fails** – verify container runtime path, images and veraPDF at `:8080`.
- **Messages stuck** – inspect RabbitMQ UI and the `*.dead-letter` queues.

## Known limitations

- Saving a revision does not yet trigger re-indexing/re-extraction.
- Worker services do not receive the user token; they use service-to-service API keys.
- The Angular production bundle exceeds its size budget (warning only).
