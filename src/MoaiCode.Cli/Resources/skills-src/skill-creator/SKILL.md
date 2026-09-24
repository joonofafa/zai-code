---
name: skill-creator
description: Create new skills, modify and improve existing skills, and measure skill performance. Use when users want to create a skill from scratch, edit, or optimize an existing skill, run evals to test a skill, benchmark skill performance with variance analysis, or optimize a skill's description for better triggering accuracy.
license: zaiCode original — principles referenced from anthropics/skills skill-creator (Apache-2.0); body self-authored (description line retained verbatim to preserve trigger behavior)
---

# Skill creator

A skill is a named instruction package the model loads on demand. This skill covers the full
lifecycle: capture what the user wants, write the SKILL.md, verify it works, and iterate. Use
it whenever the user asks to create, edit, optimize, or test a skill — even if they just say
"turn this into a skill".

## 0. zaiCode skill mechanics — read this first

Your rewritten skill must match how this product actually loads skills. Verify facts against
the source (`MoaiCode.Mcp/Skills/`) when in doubt.

- **Layout** — a skill is a directory containing `SKILL.md` with YAML frontmatter:
  ```yaml
  ---
  name: my-skill            # identifier; defaults to the directory name
  description: ...          # one line: what it does + when to use it
  license: ...              # optional; shown by the /license command
  ---
  ```
  A single loose `~/.claude/skills/my-skill.md` file also works, but prefer the directory form.
- **Discovery order** — `<workspace>/.claude/skills` → `~/.claude/skills` → `~/.zaicode/skills`
  → plugins → bundled. First hit with the same name wins; a user copy shadows the bundled one.
- **Triggering** — the model never sees SKILL.md bodies up front. It sees one line per skill,
  `name — description`, inside the `Skill` tool description. It picks a skill and calls
  `Skill(name)`; the tool returns the SKILL.md body as the tool result. So the description is
  the *only* trigger surface, and the body is all the model has once inside. Write both for
  that contract.
- **Result truncation** — tool results are capped (default 16,000 chars; `MOAI_MAX_TOOL_RESULT_CHARS`
  overrides). A SKILL.md larger than the cap is silently cut off mid-sentence. Keep the body
  well under the cap.
- **Update flow** — pick up edits live with `/skills` (reloads without restart). Bundled skills
  re-extract on version bump.
- **zaiCode house style** — user-facing strings in English; the agent you run in this workspace
  is the zaiCode CLI/TUI (no org-server, no GUI). Tailor examples and instructions accordingly.

## 1. Capture intent

Find out what the user actually wants before writing anything:

1. What should the skill enable the agent to do?
2. When should it trigger — which user phrases or contexts?
3. Expected output format or workflow steps?
4. Is the output objectively checkable (file transforms, codegen, fixed procedures) or
   subjective (prose style, design)? Objectively checkable skills deserve test prompts;
   subjective ones usually don't.

If the current conversation already demonstrates the workflow ("turn this into a skill"),
mine the history first — tools used, step order, corrections the user made — and confirm the
gaps with the user instead of re-interviewing from scratch.

## 2. Write the SKILL.md

Then draft. Get the mechanics right (frontmatter, size, single file, then interviews, then body).
For the body:

- **Description first** — it is the trigger surface. Include *what it does* and *when to use*
  in one line. Models under-trigger by default, so lean slightly pushy: name not just the
  literal keyword but the situations around it ("...use this whenever the user mentions X,
  Y, or wants Z, even without saying 'X'").
- **Imperative voice** — write instructions as commands ("Read X", "Run Y"), not "you should
  probably".
- **Explain why** — one clause of rationale per rule beats heavy MUSTs. The model follows
  reasons better than uppercase.
- **Exact templates** — when output must be structured, give a literal block the model can
  copy verbatim (headings, bullet forms, table skeletons).
- **Concrete examples** — one input→output example per tricky format. Cheap and effective.
- **Small is a feature** — the body rides in every triggered session's context. Target
  < 300 lines; every line must earn its place. If detail explodes, add a `references/`
  file per domain and point to it conditionally ("Read references/aws.md only when the
  target is AWS").
- **Safety** — never write a skill that misleads the user about its intent, exfiltrates data,
  or automates unauthorized access. If asked, refuse and say why.

Share the draft path with the user when done.

## 3. Verify

### Smoke check (always)

1. `zaiCode run "Use the <name> skill to <one test prompt>"` — in a fresh process the skill
   list is rebuilt, so this tests discovery + triggering + body, end to end.
2. Read the run transcript: did the model call `Skill(<name>)` at the right moment? Did it
   follow the body?
3. Fix and repeat. Two to three prompts is usually enough for a first pass.

### Deeper check (objectively checkable skills)

For skills whose output can be checked without taste judgments, build a small eval loop.
Keep it lightweight — this is a script plus discipline, not a framework.

1. Write test prompts the way a real user would type them, with real file names and messy
   context — not abstract one-liners. 2–3 for a first pass.
2. Run each prompt through a fresh `zaiCode run` process with the skill installed
   (the with-skill condition). To see what the skill actually adds, also run the same
   prompt with the skill disabled (`/skills` toggle, or rename the directory away) —
   that's the baseline.
3. Write assertions as concrete checks: "output CSV has exactly columns X, Y", "the test
   file compiles", "no Korean strings in user-facing output". Prefer checks you can run
   as commands (compile, parse, count) over ones requiring judgment.
4. Track results per iteration in a plain workspace directory so consecutive edits are
   comparable:
   ```
   <skill>-workspace/iteration-1/<eval-name>/with-skill/output...
   <skill>-workspace/iteration-1/<eval-name>/baseline/output...
   ```
5. Iterate: apply user feedback and assertion failures, re-run failing evals only.

### Trigger tuning (when the skill fires at the wrong moments)

If verification shows the skill triggering when it shouldn't (or not triggering when it
should), tune the description:

1. Write 5–10 realistic queries that *should* trigger and 5–8 near-miss queries that look
   similar but *should not* (adjacent domains, keyword overlap, another tool wins). The
   near-misses are the valuable ones — trivially unrelated queries test nothing.
2. For each query, ask: would this description make me load this skill? Run the genuinely
   ambiguous ones as `zaiCode run "<query>"` in a scratch directory and observe whether
   `Skill(<name>)` gets called.
3. Edit the description to cover the misses: add the missed trigger phrases, add
   discriminating detail that wards off the near-misses.
4. Re-run the misses. Stop when the scorecard is clean or the user is satisfied.

## 4. Install

Where the skill lives decides who sees it:

- **This project only** — `<workspace>/.claude/skills/<name>/SKILL.md`
- **All projects of this user** — `~/.claude/skills/<name>/SKILL.md` (or `~/.zaicode/skills`)
- **Shipped inside zaiCode itself** — `src/MoaiCode.Cli/Resources/skills-src/<name>/SKILL.md`
  + re-zip + bump `BundledSkills.Version` (see `skills-src/README.md`). Only for skills the
  product should ship to every user.

When updating an existing skill, keep its `name` and directory name unchanged — renaming
breaks `Skill(name)` calls and disables anyone's local toggle state.

## 5. Know when to stop

An iteration loop can go forever. Stop when: the user is satisfied, all assertions pass,
and the trigger set is clean — or when the remaining failures are about model ability, not
instructions. If two iterations of rewriting the same section don't fix a failure, the
problem is usually scope (skill trying to do too much) — split or narrow it instead of
polishing prose.
