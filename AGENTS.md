# Agent guide for this repository

Any AI tool or agent working here (Claude Code, Antigravity, Cursor, …):

1. **Read `.agent/STATE.md` and `.agent/tasks.md` first** — that is the shared,
   cross-tool project memory and coordination board. `TODO.md`, `docs/README.md`
   and `docs/architecture-suite/` hold the authoritative detail.
2. **Claim before you edit.** Put your name + a lease time on a task in
   `.agent/tasks.md` before touching its scope; don't edit files another agent
   holds with a fresh lease. For true parallel work, use a separate git worktree/branch.
3. **Leave it updated.** When your change lands, update `.agent/` (state + activity
   log), `TODO.md`, and any affected `docs/` — in the same commit as the code.
4. **Follow `CLAUDE.md`** — no AI-authorship traces in commits, commit and push
   continuously, and verify with real command output before claiming anything done.
