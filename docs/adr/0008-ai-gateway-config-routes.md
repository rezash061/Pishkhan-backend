# 0008. AI routes and prompts from configuration in Phase 0

Status: accepted (2026-10-28)

`IModelRouter.GetChatClient(task)` resolves routes from `Ai:Routes:{task}` through `IOptionsMonitor`, so a route change applies without a restart (verified by a unit test). Each route has provider, model, parameters and timeout; routes form a fallback chain with a circuit breaker per provider. Prompts are versioned under `Ai:Prompts:{key}:{version}` with `Ai:ActivePrompts`. In Phase 1 the same shapes are fed from `backoffice.ai_model_routes` and `backoffice.prompt_templates`, with activation gated by an eval run. A `fake` provider kind gives deterministic answers for development, tests and dummy eval runs.
