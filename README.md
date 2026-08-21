# Nexora

**Status:** Approved implementation baseline  
**Current milestone:** Phase 0 — Repository and engineering readiness  
**Documentation baseline:** Frozen for Phase 0 backend implementation  
**Last updated:** 2026-08-21

Nexora is an AI-powered interview-practice web application. Its primary journey is **CV/JD → personalised mock interview → rubric/evidence-based feedback → report → further practice**. The production MVP is a coaching product, not a covert copilot for real interviews.

The current application is a static HTML/CSS/JavaScript frontend hosted on Vercel. The next milestone is Phase 0/Phase 1 backend implementation.

## Fixed implementation baseline

- .NET 10 LTS, ASP.NET Core 10, Entity Framework Core 10 and PostgreSQL.
- ASP.NET Core Identity owns credentials and roles.
- Static frontend consuming REST JSON under `/api/v1`.
- Modular monolith with `Nexora.Api`, `Nexora.Business`, `Nexora.Data`, `Nexora.Integrations` and `Nexora.Worker`; no microservices or Clean Architecture project sprawl.
- Presentation → Business → Data; all AI, payment and storage access goes through provider-neutral adapters.
- Canonical interview lifecycle, quota ledger rules, security invariants and release gates are linked from [SPEC.md](SPEC.md).

## Start here

1. Coding agents must read [AGENTS.md](AGENTS.md).
2. All implementers should read the concise [implementation specification](SPEC.md).
3. Locate formal requirement IDs in [docs/SRS.md](docs/SRS.md).
4. Follow the [detailed documentation index](docs/README.md) for the affected contract or decision.

## Deferred production enablement decisions

DEC-01 through DEC-04 do **not** block backend or local development, Phases 0–3, or integration testing with fake/development adapters. They block only the corresponding real production capability:

- **DEC-01:** production AI provider/model and production AI budgets.
- **DEC-02:** production Vietnamese payment provider and refund/invoice/tax policy.
- **DEC-03:** final retention periods and approved legal/privacy text.
- **DEC-04:** production hosting vendors, domains, mail provider and infrastructure accounts.

Development may use `FakeAiProvider`, a configuration-driven `GeminiAiProvider`, `FakePaymentProvider`, and `LocalStorageProvider` or another development storage adapter. None is thereby selected as the final production provider.

## Next milestone

Begin [Phase 0 — repository and engineering readiness](docs/10-delivery-plan.md), followed by Phase 1 foundation. Production provider selection is not a prerequisite. No backend projects currently exist; when they are created, the canonical build checks are `dotnet restore`, `dotnet build` and `dotnet test`.

