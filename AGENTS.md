# AI Palm - Codex workflow

## Validation workflow

Whenever you inspect, modify, review, or refactor code:

1. Identify the appropriate validation commands for the project.
2. Run the relevant tests, lint checks, static analysis, or build commands.
3. Send command output to `reflex_scan`.
4. Send the result to `reflex_stop`.
5. If `stop=false`, call `reflex_extract`.
6. Use the extracted error summary to fix the problem.
7. Re-run validation.
8. Repeat until `reflex_stop` returns `stop=true`.

## ReflexGate tools

Use these MCP tools when available:

- `reflex_scan`
  - Check logs/output for errors, warnings, secrets, crashes, or suspicious output.

- `reflex_extract`
  - Extract the important error/status information from large command output.

- `reflex_stop`
  - Decide whether validation has completed successfully.

## Rules

- Do not assume code is correct only because it looks correct.
- Prefer running a real test, lint, build, or syntax check.
- After changing source code, validate the affected area.
- If a full test suite exists, run it before considering the task finished.
- Do not expose secrets or credentials in chat.
- If `reflex_scan` reports a secret leak or critical issue, stop and report it.
- Do not declare the task complete until validation succeeds or clearly explain why validation could not be run.
