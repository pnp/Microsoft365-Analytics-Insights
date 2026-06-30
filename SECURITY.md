# Security Policy

The maintainers of **Microsoft 365 Advanced Analytics** take the security of the
software seriously. This covers the data-collection engine (the Activity and App
Insights importer web jobs), the admin web app, the Windows installer
(`App.ControlPanel`), and the Power BI report templates in this repository.

If you believe you have found a security vulnerability in this project, please
report it to us privately as described below. **Please do not report security
vulnerabilities through public GitHub issues, discussions, or pull requests.**

## Supported versions

Security fixes are provided for the most recent release published from the
`main` branch. We always recommend running the latest available
[release](https://github.com/pnp/Microsoft365-Analytics-Insights/releases).

| Version                    | Supported                |
| -------------------------- | ------------------------ |
| Latest `main` release      | :white_check_mark:       |
| Pre-release / `dev` builds | :white_check_mark: (best effort) |
| Older releases             | :x:                      |

## Reporting a vulnerability

Please use **GitHub's private vulnerability reporting** for this repository:

1. Go to the **Security** tab of the repository.
2. Select **Report a vulnerability** ("Privately report a vulnerability").
3. Provide a clear description, the affected component(s), and reproduction
   steps.

> **Maintainers:** if the "Report a vulnerability" button is not visible, enable
> **Private vulnerability reporting** under *Settings → Code security and
> analysis* so that reporters can use this channel.

To help us triage quickly, please include where possible:

* The component affected (importer web job, web app, installer, reports, etc.).
* The type of issue (e.g. credential exposure, injection, privilege escalation,
  insecure storage of secrets).
* Step-by-step instructions to reproduce the issue.
* Proof-of-concept or exploit code, if available.
* The impact, including how an attacker might exploit the issue.

Please **do not** include real tenant data, customer information, or live
credentials / connection strings in your report — redact them or use
placeholders.

## What to expect

* We aim to acknowledge new reports within **5 business days**.
* We will keep you informed as we investigate and validate the issue.
* We ask that you give us a reasonable opportunity to release a fix before any
  public disclosure (coordinated disclosure).
* We are happy to credit reporters in the release notes unless you prefer to
  remain anonymous.

## Scope

This policy covers the source code and the build/release artifacts produced by
this repository. It does **not** cover:

* The security configuration of *your own* Azure subscription or Microsoft 365
  tenant where you deploy the solution (for example how you scope service
  principal permissions, secure your SQL database, or store secrets). See the
  [wiki](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki) for
  deployment and permissions guidance.
* Vulnerabilities in third-party dependencies — please report those to the
  relevant upstream project. A heads-up is still welcome so we can pick up the
  update.
