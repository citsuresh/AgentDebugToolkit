# Copilot Instructions

<!-- project-memory-management-graph: skill-version=24 -->
## Persistent Project Memory

This repo uses the `project-memory-management-graph` skill for persistent, low-token project
memory plus Roslyn-based code knowledge graphs. Bootstrap fully regenerates this section;
Project Guidelines and Response Guidelines are merged additively without removing existing rules.

- If they exist, read `docs/CODE_SUMMARY.md` and `docs/DESIGN_DECISIONS.md` before exploring
  the codebase with search tools for a new task. Missing files permit normal exploration.
- Read `docs/PROJECT_STATE.md` and `docs/ROADMAP.md` when the user asks "do you remember",
  references prior work, or asks what's next, if those files exist.
- Read `docs/domain-lookup-patterns.md`, if present, when the task depends on naming, business
  logic, or domain conventions not represented in the graph, before manual exploration.
- Read `docs/FEATURE_CHECKLIST.md`, if present, when planning or adding a feature; include its
  items in the breakdown and check parity during audit. Read `docs/KNOWN_OPEN_FINDINGS.md`
  when investigating an unexpected issue. These files are user-curated: never create or edit
  them without explicit user direction.
- Registered solution graphs and dependencies are listed below. Query via the confirmed wrapper
  `C:\MyFiles\Git\GraphTools\tools\Invoke-GraphTools.ps1 -Tool Query -- <args>`; never read a
  graph wholesale.

  | Solution path relative to repo root | Slug | Graph file | Dependencies file |
  | --- | --- | --- | --- |
  | `AgentDebugToolkit.slnx` | `agentdebugtoolkit` | `docs/full-graph.agentdebugtoolkit.json` | `docs/project-dependencies.agentdebugtoolkit.json` |

- Graph-first is the default: before general text/symbol search to locate any C# class,
  interface, enum, or member, find definitions/callers/callees, or relate types, query the
  registered graph. This includes conceptual requests such as "explain X" or "how does X work"
  whenever answering requires identifying or relating specific C# symbols.
- Query the current IDE solution graph first only when the solution is determinable; otherwise
  treat it as unknown. Check other registered graphs if needed, label each result by solution,
  and note symbols found in multiple solutions.
- If no registered graph covers the question, a graph file is missing, a query fails, or no
  graph has the answer, say so explicitly (include the full error for failures), then fall back
  to normal search. A "symbol not found" result may mean the graph is stale. Non-code/file-layout
  questions may also require fallback. Never fall back silently or treat absence as a blocker.
  Mention recurring graph/domain friction briefly if encountered; do not search for it proactively.
- Update `docs/CODE_SUMMARY.md` for new projects or structural classes/services, changed component
  responsibilities, or dependency changes, not routine fixes. Keep its pointer to `docs/KEY_FLOWS.md`;
  never add Key Flows entries directly to the summary.
- Update `docs/KEY_FLOWS.md` only for newly traced and confirmed end-to-end flows spanning multiple
  C# symbols, as short arrow-chains. Keep domain details in `docs/domain-lookup-patterns.md`; add a
  one-line pointer only when a relevant entry already exists. Skip when no new flow was traced.
- Append dated entries to `docs/DESIGN_DECISIONS.md` for non-obvious design choices, rejected
  alternatives with reasons, or reversed decisions. Never delete prior entries.
- Overwrite `docs/PROJECT_STATE.md` at session end with current focus, open tasks, and recently
  changed files. Update `docs/ROADMAP.md` only for deliberate priority/plan changes. Keep memory concise.
- Reusable scripts: before writing code for a repeatable mechanical step (filtering a log, applying the same edit to many files, extracting data from several files), check `docs/SCRIPTS.md` if it exists and run a script that fits, as its index line says; if the index line says it modifies or deletes files, ask the user before each run, and check its output before relying on it. If you wrote working code for such a step that is likely to be reused with different inputs, is more than a one-liner, can take its inputs as parameters, and contains no credentials, tokens, customer data or personal details, save it as `scripts/agent/<verb-noun>.ps1` with a header comment (purpose, parameters, whether it modifies anything), add one line to `docs/SCRIPTS.md` (table: script | purpose | how to call | read-only or modifies | date; create the file with the table header if it is missing), and say so in your reply. Before saving, check whether an existing script in `scripts/agent/` can take the new case as a parameter. If one can, extend that script with a backward-compatible parameter (its default keeps the current behaviour, and existing parameter names, order and output format stay unchanged), update its header comment, index line and date, and say so, rather than saving a near-copy. If the task's file rules forbid changing the existing script, do not save a near-copy; say in your reply which parameter the existing script needs. Every part that varies between uses (paths, patterns, comment markers, row counts, exclusions) must be a parameter. For C# projects, skip `obj` and `bin` folders by default, with a switch to include them. Before finishing a task, check once whether you wrote any script worth saving. If a script fails or is out of date, fix it in place and update its date, or do the step normally; never loop on a broken script. When a task runs a listed script, set its date in `docs/SCRIPTS.md` to today if the task's file rules allow editing that file; otherwise leave the date unchanged and say so in your reply. The user curates these files: never delete scripts or index lines on your own.

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
- Prefer existing repository scripts wherever they cover the requested operation, including
  process management, builds, validation, and cleanup. Check `docs/SCRIPTS.md` and the relevant
  script before composing manual commands; reuse it rather than repeating its steps inline.
  Obtain approval before each modifying run and honor task restrictions; never use a script to
  bypass a prohibited action. If no script fits, or it fails, explain the gap before using manual
  commands. `scripts\agent\Rebuild-Mcp.ps1` is the preferred approved Claude-close/MCP-rebuild
  workflow, but builds MCP only: UI CLI changes still require a UI project or full solution build
  and output timestamp verification. Do not run it when process termination is prohibited.
- When the user asks to publish AgentDebugToolkit, run the repository script
  `scripts\agent\Publish-AgentDebugToolkit.ps1` with `-Confirm:$false`, using its default
  Release configuration and `C:\Shared\AgentDebugToolkit-win-x64` destination unless the user
  specifies otherwise. Use `-ReplaceExisting` if the destination exists; the script retains the
  previous package as a backup. Treat the explicit publish request as approval for that invocation, while
  honoring any additional task restrictions. Check the script and `docs/SCRIPTS.md` first,
  verify the result, and report failures without changing NuGet sources or stopping processes.
- At the very first user message in a new chat session (not on subsequent prompts within the
  same session), automatically invoke the `project-memory-management-graph` skill's Begin
  Session step before addressing the user's request — without asking for confirmation. Skip
  this only if the first prompt is clearly self-contained and unrelated to this codebase
  (e.g. general syntax/language questions, IDE/tool questions, or generic advice not
  requiring project context). If in doubt, run it — it is a cheap check.
- If a change in this repo implies the `ui-debug-map` or `agent-orchestrator` Copilot skill needs
  updating (new verb, changed CLI contract, new gotcha discovered, etc.), edit the versioned copy
  at `C:\MyFiles\Git\Visual-Studio-Copilot-Skills\<skill-name>\SKILL.md` — never edit
  `%USERPROFILE%\.copilot\skills\<skill-name>\SKILL.md` directly. The global `.copilot\skills`
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
- If the `vs-chat-history` skill is installed, use it to search earlier Visual Studio
  Copilot Chat conversations on this machine (for example "what did we decide about X",
  "which chat worked on Y"). It reads `session-store.db` only, and chat text is
  confidential: show short excerpts only. Ignore this note if the skill is not installed.

## Response Guidelines

Default to concise, minimal replies: no filler, no restating the question, no unnecessary
preamble. Provide full detail only in these cases:

- Design rationale discussions.
- Build/error diagnosis.
- Before any destructive action.
- When multiple approaches exist.
- When generating docs/*.md content itself.
