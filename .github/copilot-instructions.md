<!-- project-memory-management-graph: skill-version=10 -->
# Copilot Instructions

## Persistent Project Memory

This repo uses the `project-memory-management-graph` skill for persistent, low-token project
memory plus a Roslyn-based code knowledge graph.

- If it exists, read `docs/CODE_SUMMARY.md` and `docs/DESIGN_DECISIONS.md` before exploring the
  codebase with search tools for a new task. If these files do not exist, fall back to normal
  exploration — their absence is not an error.
- If it exists, read `docs/PROJECT_STATE.md` and `docs/ROADMAP.md` when the user asks "do you
  remember", references prior work, or asks what's next.
- If it exists, read `docs/domain-lookup-patterns.md` when a task requires domain conventions,
  naming schemes, or business logic that the graph doesn't represent — check it before falling
  back to manual exploration or a fresh graph query.
- If it exists, `docs/full-graph.json`/`docs/project-dependencies.json` are available and should
  be queried via the GraphTools wrapper script
  (`C:\MyFiles\Git\GraphTools\tools\Invoke-GraphTools.ps1`, invoked as
  `<wrapper> -Tool Query -- <args>`) (never read wholesale). This is a default, not a judgment
  call: before using a general-purpose search tool (text search, symbol search, grep, or
  similar) to locate a class/interface/enum, find a method's definition, find its callers, find
  its callees, check how two types relate, or otherwise answer "where is X" / "what uses X" for
  anything that is a C# symbol, first check whether `docs/full-graph.json` exists in this
  project, and if so, query it via the wrapper instead of a general search tool. This applies
  even to a simple "find this file/class" request, not only explicit call-graph or architecture
  questions. This preference applies regardless of how the question is phrased:
  conceptual/explanatory framings ("explain X", "walk me through X", "how does X work",
  "describe the Y flow") are NOT exempt just because they aren't literally worded as a
  find/locate request. The test is whether answering the question requires locating,
  identifying, or relating specific named C# classes/interfaces/methods — not whether the
  question is phrased as a lookup. If it does, query the graph first.
- Fallback: if `docs/full-graph.json` does not exist, if `GraphTools.Query.exe` errors or exits
  non-zero, or if the graph doesn't contain an answer to the specific question (e.g. the
  question is about non-code content, file layout, or something the graph doesn't track), fall
  back to normal search tools and proceed — do not treat a missing/failed graph query as a
  blocking error.
- If, during graph-first or domain-lookup work, you notice a recurring friction point, mention
  it briefly at the end of your response — don't act on it, just note it.
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
  each session.
- Keep all memory files concise — they exist to reduce token usage on future re-reads, not to
  serve as exhaustive documentation.

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

## Response Guidelines

Default to concise, minimal replies: no filler, no restating the question, no unnecessary
preamble. Provide full detail only in these cases:

- Design rationale discussions.
- Build/error diagnosis.
- Before any destructive action.
- When multiple approaches exist.
- When generating docs/*.md content itself.
