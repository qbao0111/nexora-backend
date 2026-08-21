# Nexora Documentation Index

**Status:** Approved implementation baseline  
**Baseline:** Frozen for Phase 0 backend implementation  
**Last updated:** 2026-08-21

Start at the repository [README](../README.md). Coding agents must read [AGENTS.md](../AGENTS.md), then the concise [implementation specification](../SPEC.md). This directory contains detailed sources; each has one primary responsibility.

| Document | Canonical responsibility |
| --- | --- |
| [00-market-research.md](00-market-research.md) | Product research and positioning. |
| [01-requirements.md](01-requirements.md) | Product-level PRD and priorities. |
| [SRS.md](SRS.md) | Formal product/system behavior, testable requirements, priorities and business rules. |
| [PROJECT-SPEC.md](PROJECT-SPEC.md) | Detailed technical specification. |
| [02-architecture.md](02-architecture.md) | Three-Layer modular-monolith architecture and patterns. |
| [03-api-data-contract.md](03-api-data-contract.md) | HTTP routes, request/response contracts, errors and idempotency conventions. |
| [04-production-runbook.md](04-production-runbook.md) | Deployment, production operations and go-live gates. |
| [05-test-strategy.md](05-test-strategy.md) | Test strategy and the only canonical Definition of Done. |
| [06-security-privacy.md](06-security-privacy.md) | Threat model and privacy controls. |
| [07-architecture-decisions.md](07-architecture-decisions.md) | Approved architecture choices/rationale and DEC-01–04 production decision gates. |
| [08-data-model.md](08-data-model.md) | Persisted entities/schema/state representations, subject to SRS behavior and business rules. |
| [09-ai-integration-spec.md](09-ai-integration-spec.md) | AI provider contract, validation, rubric and jobs. |
| [10-delivery-plan.md](10-delivery-plan.md) | Implementation phases and readiness. |
| [11-analysis-design-models.md](11-analysis-design-models.md) | Use cases, diagrams and traceability. |

## Deferred decisions are production gates, not engineering blockers

- **DEC-01:** production AI provider/model and production budgets.
- **DEC-02:** production Vietnamese payment provider and refund/invoice/tax policy.
- **DEC-03:** final retention periods and approved legal/privacy text.
- **DEC-04:** production hosting vendors, domains, mail provider and infrastructure accounts.

These decisions do not block backend/local development, Phases 0–3 or integration testing using fake/development adapters. They block enabling only the corresponding real production capability. The canonical wording is in [07-architecture-decisions.md](07-architecture-decisions.md#production-enablement-decisions-dec-01-through-dec-04).
