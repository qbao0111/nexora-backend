# Nexora Documentation Index

**Status:** Approved implementation baseline and internal-development navigation
**Baseline:** Frozen specification; implementation guides evolve with verified repository capabilities
**Last updated:** 2026-08-25

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
| [development-setup.md](development-setup.md) | Fresh-machine teammate setup, secret configuration, local runtime and troubleshooting. |
| [frontend-integration.md](frontend-integration.md) | Browser/API handoff, auth, polling, idempotency and core FE journey. |
| [sepay-sandbox.md](sepay-sandbox.md) | Internal SePay Sandbox setup, IPN/reconciliation flow and safety notes before DEC-02 production payment approval. |

## Deferred decisions are production gates, not engineering blockers

- **DEC-01:** production AI provider/model and production budgets.
- **DEC-02:** production Vietnamese payment provider and refund/invoice/tax policy.
- **DEC-03:** final retention periods and approved legal/privacy text.
- **DEC-04:** production hosting vendors, domains, mail provider and infrastructure accounts.

These decisions do not block backend/local development or the internal Gemini, fake-payment and local-storage adapters. They block enabling only the corresponding real production capability. The canonical wording is in [07-architecture-decisions.md](07-architecture-decisions.md#production-enablement-decisions-dec-01-through-dec-04).
