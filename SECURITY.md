> 🇫🇷 [Version française](SECURITY.fr.md)

# Security Policy

## Supported Versions

Only the latest published release of Orkeon Studio receives security fixes.

## Reporting a Vulnerability

**Please do not open a public issue for security vulnerabilities.**

Report vulnerabilities through [GitHub Private Vulnerability Reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) on this repository ("Security" tab → "Report a vulnerability").

What to include:

- A description of the vulnerability and its impact.
- Steps to reproduce (proof-of-concept settings file or crew if possible).
- The affected application (`orkeon-studio`, `orkeon-studio-config`, `orkeon-studio-run`) and its platform.

We aim to acknowledge reports within **72 hours** and to provide a remediation plan or fix within **30 days** for confirmed issues.

## Scope

Orkeon Studio is a front-end: it edits settings, and it launches and reads the output of the separately installed `orkeon` CLI. It never runs a crew in-process.

- A flaw in Studio itself (how it writes settings, stores or passes secrets, resolves the CLI, handles the CLI's output) belongs here.
- A flaw in the engine, the CLI, the tools or the LLM providers belongs to the [Orkeon repository](https://github.com/Orkeon/orkeon). Its [security policy](https://github.com/Orkeon/orkeon/blob/main/SECURITY.md) also holds the threat model for LLM-driven tool execution and the hardening recommendations, which apply to every crew Studio launches.

## Hardening Recommendations

- Install Studio from the GitHub Release assets and check them against the `SHA256SUMS` file published with them (`sha256sum -c SHA256SUMS`) before running anything.
- Install the `orkeon` CLI from a source you trust, and set `ORKEON_CLI_DIR` only to a directory you control: Studio runs the binary it finds there.
- Keep secrets out of settings files you share. A secret belongs in an environment variable, or in the key store Studio offers.
- Review the mounts and the tool rights a crew gets before launching it: the settings Studio edits are the ones the CLI enforces.
