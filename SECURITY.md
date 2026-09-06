# Security Policy

AI Palm processes untrusted prompts on volunteer-owned computers. Security reports must not be filed as public issues when they could expose users, secrets, or infrastructure.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting feature for this repository. Include affected versions, impact, reproduction steps, and a minimal proof of concept where possible.

Do not include real API keys, platform keys, personal data, or prompts in a report. Please allow maintainers time to investigate before public disclosure.

## Current safety boundaries

- Do not use the public network for confidential or personal information.
- Local model API keys should never be sent to the AI Palm coordinator.
- Host applications make outbound connections and should not expose local inference ports publicly.
- Model output is untrusted and must never be executed automatically.
