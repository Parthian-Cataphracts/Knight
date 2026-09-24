# AGENTS.md — the contract for every AI agent on this repository

This file is the **single, tool-agnostic source of truth** for any AI agent or tool
working here — Claude Code, opencode (DeepSeek), Antigravity (Google), Hermes (Qwen),
Cursor, or a human. It is committed to git, so it is the same for everyone, on every
machine, across every session. If your tool does not read this file automatically,
you (the operator) must point it here first.

Read this whole file before doing anything. It is short on purpose.

---

## 1. Start here — reload the project in 30 seconds

Read these, in order, before touching anything. They are the shared brain; do not
re-read the entire codebase to orient — that is what these are for:

1. **`.agent/STATE.md`** — what the project is, the current goal, the phase. THE
   starting point.
2. **`.agent/tasks.md`** — the live task board: who is doing what, on which branch,
   with a lease. This is how concurrent agents avoid collisions.
3. **`.agent/activity.md`** — recent log of what changed and by whom.
4. **`.agent/decisions.md`** — durable decisions and why.
5. For detail: **`TODO.md`**, **`docs/README.md`**, **`docs/architecture-suite/`**,
   and the code map in **`graphify-out/GRAPH_REPORT.md`** (open `graphify-out/graph.html`
   to browse it). Use the graph to find *where* things live instead of reading everything.

## 2. The golden rules (what to change, what not)

- **Never work directly on `main`.** Every agent works on its **own git branch or
  worktree** (`agent/<tool>-<short-task>`, e.g. `agent/opencode-loyalty-fix`). This
  is what makes three agents on three tools safe at once: their edits live on
  separate branches and can never overwrite each other in the working tree. Merge to
  `main` only through review (rule 4).
- **Claim before you edit.** In `.agent/tasks.md`, put your agent name, your branch,
  and a lease time on a task before touching its files. If another task's scope
  overlaps yours and its lease is still fresh, do **not** touch those files — pick
  other work or coordinate. Stay inside your task's declared scope; if you must
  touch a file outside it, update the scope first.
- **Follow `CLAUDE.md`.** It is authoritative: no AI-authorship traces in commits,
  commit and push continuously, and never claim work done without real command
  output proving it.
- **Do not touch, without an explicit task saying so:** `appsettings.Production.json`
  and any secrets/credentials; the deploy path (`knightctl`) and live server; the
  base-store vs. Feature boundary (ADR 0024); anything the task board shows another
  agent holds.

## 3. Prove your work (no self-approval)

A task is `VERIFIED` only when terminal output proves it — never from memory. Run the
tests (`dotnet test tests/Knight.UnitTests`; the Python feature tests where relevant),
the build, and `git diff --stat`, and read the output. Tests fail or the diff does not
match the intent → the task is `REJECTED`/`FAILED`, fix it. Paste the proving line
into `.agent/tasks.md`. Passing tests mean "it runs", not "it is good" — see rule 4.

## 4. Review before merge (this is how weak/mixed models stay safe)

No agent merges its own branch to `main` unsupervised. Before merge, a review pass —
ideally by a strong model — reads the branch `git diff` against this project's rules
and conventions and decides: merge, request refactor, or revert. `task-controller`
answers "did it work?"; the review (e.g. `/code-review` in Claude Code, or a strong
model reading the diff + `docs/` + the graph) answers "is it good and does it fit?".
If a merge already broke `main`, `git revert`/`git reset` to the last good commit —
git is the undo, and `.agent/activity.md` tells you what was touched.

## 5. Leave it updated (cheap, do it every task)

When your change lands, in the **same commit**:
- update `.agent/STATE.md` (if the goal/phase moved), append to `.agent/activity.md`,
  and set your task's status in `.agent/tasks.md`;
- update `TODO.md`, `README`, and any affected `docs/`.

These are plain-text edits — nearly free. Stale docs are worse than none.

**The knowledge graph is different:** do **not** rebuild it every task. Run
`graphify <path> --update` (incremental, cheap — only changed files) **only after a
structural change** (new files/modules). A full `graphify` rebuild is a one-time /
rare thing.

## 6. Running several agents/tools at once

The safe pattern, in one line: **one branch per agent + claim on the board + review
before merge.**

- Each agent: its own branch/worktree, its own claimed task with non-overlapping
  scope. File collisions become impossible in the tree; anything that does conflict
  surfaces as a normal git merge conflict a human/strong-model resolves — far better
  than silent overwrites.
- The board (`.agent/tasks.md`) is the shared coordination point at the project level.
- Merges are serialized through review on `main`.

## 6b. Per-tool entry (so each tool actually reads this)

The shared brain is these repo files; each tool just needs a thin pointer to them:
- **Claude Code** — `CLAUDE.md` (+ its skills) already points here.
- **opencode** — reads `AGENTS.md` natively. Nothing to do.
- **Antigravity / Cursor / others** — add a one-line rule in that tool's own config
  ("Read AGENTS.md first and follow it") if it does not read `AGENTS.md` on its own.
