# Copilot Instructions

<!-- project-memory-management-graph: skill-version=14 -->
## Persistent Project Memory

This repo uses the `project-memory-management-graph` skill for persistent, low-token project
memory plus Roslyn-based code knowledge graphs.

- If it exists, read `docs/CODE_SUMMARY.md` and `docs/DESIGN_DECISIONS.md` before exploring the
  codebase with search tools for a new task. If these files do not exist, fall back to normal
  exploration — their absence is not an error.
- If it exists, read `docs/PROJECT_STATE.md` and `docs/ROADMAP.md` when the user asks "do you
  remember", references prior work, or asks what's next.
- Read `docs/domain-lookup-patterns.md` when a task depends on domain conventions not represented
  in the graph. Read `docs/FEATURE_CHECKLIST.md` when planning or adding a feature, include its
  items in the proposed breakdown, and check parity during audit. Read `docs/KNOWN_OPEN_FINDINGS.md`
  when investigating an unexpected issue. The checklist and open findings are user-curated; never
  create or edit them without explicit user direction.
- Registered solution graphs and dependencies are listed below. Query graphs through
  `C:\MyFiles\Git\GraphTools\tools\Invoke-GraphTools.ps1 -Tool Query -- <args>`; never read a
  graph wholesale.

  | Solution path relative to repo root | Slug | Graph file | Dependencies file |
  | --- | --- | --- | --- |
  | `AgentDebugToolkit.slnx` | `agentdebugtoolkit` | `docs/full-graph.agentdebugtoolkit.json` | `docs/project-dependencies.agentdebugtoolkit.json` |

- Before using general search to locate a C# type/member, find its definition/callers/callees,
  or understand type relationships, query the registered graph first. Query the current IDE
  solution graph first only when that solution is determinable; otherwise query registered
  graphs and label each result by solution, noting symbols found in multiple.
- If no registered graph covers the question, a graph file is missing, a query fails, or no
  graph has the answer, say so explicitly (include the full error for failures), then fall back
  to normal search. A "symbol not found" result may mean the graph is stale. If graph/domain
  investigation reveals recurring friction, mention it briefly at the end; do not proactively
  search for such issues.
- Update `docs/CODE_SUMMARY.md` when: a new project is added, a new structural class/service is
  added, a component's responsibility changes, or a project/component dependency changes. Do not
  update for routine bug fixes or small edits that don't affect structure. Ensure the pointer
  line to `docs/KEY_FLOWS.md` stays present; do not add Key Flows entries directly to
  `docs/CODE_SUMMARY.md`.
- Update `docs/KEY_FLOWS.md` directly (not `docs/CODE_SUMMARY.md`) when a new end-to-end flow
  spanning multiple C# symbols is fully traced and confirmed during the session: add it as a
  short arrow-chain (e.g., `A -> B -> C -> D`), consistent with existing entries. Skip if no
  such flow was traced.
- Update `docs/DESIGN_DECISIONS.md` (append-only, dated entries) when: a non-obvious
  architectural/design choice is made, an alternative approach is rejected with a reason, or a
  past decision is reversed. Never delete prior entries.
- Update `docs/PROJECT_STATE.md` at the end of a working session to reflect current focus, open
  tasks, and recently changed files (overwrite, not append).
- Update `docs/ROADMAP.md` only when priorities/plans deliberately change, not automatically
  each session. Keep memory files concise.

## Regression Auditor Protocol

For code changes, use an independent regression auditor after each meaningful part and again
at the end. The auditor reviews unintended behavior/caller/shared-state changes, contradictions
or duplication, and runs available builds/tests. If `docs/FEATURE_CHECKLIST.md` exists, check
parity against it. Each in-scope finding must give the root cause, an effort/risk estimate, and a
suggested diff only; out-of-scope findings are report-only, with no estimate or fix suggestion.
The final audit also runs the full build/test suite, verifies docs synchronization, and checks
cross-cutting consistency. Surface the full report; never apply a suggested fix without the
user's explicit, individual approval.

Before a non-trivial multi-part change, propose the smallest independently reviewable breakdown
and get confirmation before coding; include feature-checklist items. If parts are too coupled,
explain and propose combining them. The auditor reviews the diff independently, without the
builder's rationale. If the same underlying issue recurs across two or more attempted fixes, stop
and recommend a fresh focused context. Judge quality against project conventions, explicit
criteria, or passing tests, in that order; reports state checks/findings as facts, never with
overall confidence claims.

A user may opt out for the current session: by default this suspends both audits and pre-build
decomposition. If the request targets only one, clarify; never persist the opt-out. Reduced
cadence is a separate session-scoped option: keep decomposition, audit once at each confirmed
phase boundary, and revert to per-part audits for the remainder after a real in-scope finding.
The final phase audit is also the final audit; if no phases were agreed up front, use full
per-part cadence. Neither mode waives the full-report or individual-fix-approval rules.

## Project Guidelines

- Manual commit review before any commit.
- Build/test verification after every change.
- Do not commit or push automatically — wait for explicit user confirmation first.
- At the very first user message in a new chat session (not on subsequent prompts within the
  same session), automatically invoke the `project-memory-management-graph` skill's Begin
  Session step before addressing the user's request — without asking for confirmation. Skip
  this only if the first prompt is clearly self-contained and unrelated to this codebase
  (e.g. general syntax/language questions, IDE/tool questions, or generic advice not
  requiring project context). If in doubt, run it — it is a cheap check.
- If a change in this repo implies the `ui-debug-map` or `agent-orchestrator` Copilot skill needs
  updating (new verb, changed CLI contract, new gotcha discovered, etc.), edit the versioned copy
  at `C:\MyFiles\Git\Visual-Studio-Copilot-Skills\<skill-name>\SKILL.md` — never edit
  `C:\Users\sveluswa\.copilot\skills\<skill-name>\SKILL.md` directly. The global `.copilot\skills`
  copy is the installed/active copy Copilot actually loads at runtime; it is not version controlled
  and should only ever be updated by running `Install-Skills.ps1` from the repo, after the repo
  change has been reviewed and committed — never edited in place.
- After editing the repo copy, stop and show the diff for review before doing anything else. Do not
  run `Install-Skills.ps1` unless explicitly asked to — that is the user's call after reviewing the
  skill change separately from the AgentDebugToolkit change that triggered it.
- This project has a WinForms/WPF UI. If you need to debug it in Visual Studio, use the `vs-debug`
  skill (also independently useful for debugging tasks not reached via UI navigation). If you need
  to navigate through the running application to reach a specific screen or feature — including as
  part of debugging it — use the `ui-navigation-orchestrator` skill, which composes `vs-debug` and
  `ui-interaction` for you.

## Response Guidelines

Default to concise, minimal replies: no filler, no restating the question, no unnecessary
preamble. Provide full detail only in these cases:

- Design rationale discussions.
- Build/error diagnosis.
- Before any destructive action.
- When multiple approaches exist.
- When generating docs/*.md content itself.
