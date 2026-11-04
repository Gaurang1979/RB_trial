# Owner-Only Manual Merge Gate for All Agents

- Status: done
- Session: owner-manual-merge-rule, 2026-11-03
- Pull request: open for owner manual merge
- Section 5 item: governance
- Components/files: docs/REQUIREMENTS-VISION-SYSTEM.md, CLAUDE.md, AGENTS.md, GEMINI.md
- Depends on: none

## Summary
Enforces the mandatory directive established by the owner on 2026-11-03:
"do not merge directly anything on main. i will check and manually update always."

1. **Common Requirement Document Updated**:
   - Added `REQ-05` (Strict Prohibition on Direct Merging to Main) to Section 17.6 in `docs/REQUIREMENTS-VISION-SYSTEM.md`.
2. **CLAUDE.md Updated**:
   - Codified in Rule 0 and Step 6: No agent may execute `gh pr merge`, push directly to `main`, or merge pull requests. All pull requests must be left open for owner inspection and manual merge.
3. **Multi-Agent Rules (`AGENTS.md` and `GEMINI.md`)**:
   - Added Section 5 (Owner-Only Merge Gate) in `AGENTS.md` and Core Rule 2 in `GEMINI.md`.
