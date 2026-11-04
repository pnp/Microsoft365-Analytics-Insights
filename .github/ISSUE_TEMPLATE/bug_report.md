---
name: Bug report
about: Create a report to help us improve
title: 'Bug: <title>'
labels: bug,'review needed'
assignees: ''
---

<!--
⚠️ THIS REPOSITORY IS PUBLIC, AND ISSUES ARE PERMANENT. Anyone can read what you post here, and deleting it later doesn't remove every copy.

Before you submit, remove or replace:
- organisation and tenant names, domain names and tenant IDs;
- people's names, user principal names and email addresses;
- GUIDs, URLs (including SharePoint site and file URLs), Azure resource names, database and server names;
- connection strings, secrets and keys.

Use obviously fake values instead, such as "Contoso" and 00000000-0000-0000-0000-000000000000. Check screenshots and log lines the same way.

Found a security vulnerability? Don't report it here. Report it privately, as SECURITY.md describes:
https://github.com/pnp/Microsoft365-Analytics-Insights/security/policy
-->

## Describe the bug

<!--
A clear and concise description of what the bug is.
-->

## Expected behavior

<!--
A clear and concise description of what you expected to happen.
-->

## To Reproduce

<!--
Steps to reproduce the behavior:
-->

1. Go to '...'
2. Click on '....'
3. Scroll down to '....'
4. See error

## Build

<!--
The build you're running. In the web portal, open Administration > Service health: the page title shows it, for example "System Health - Build 1234".
If you can't open the portal, give the release you installed, from the GitHub releases page.
-->

## Component

<!-- Tick every part that's involved. -->

- [ ] Installer (`AnalyticsInstaller.exe`)
- [ ] Database upgrade or manual SQL upgrade script
- [ ] Office 365 activity importer (WebJob `Office365ActivityImporter`)
- [ ] App Insights importer (WebJob `AppInsightsImporter`)
- [ ] Web portal
- [ ] SharePoint page tracker (`AITracker.js` or the SPFx extension)
- [ ] Power BI reports
- [ ] Documentation
- [ ] Not sure

## What changed recently

<!--
Anything that changed before the problem started: an upgrade (from which build to which), a permission or consent change, an installer configuration change, a network or policy change. "Nothing" is a useful answer too.
-->

## Logs

<!--
The relevant, REDACTED log lines or error messages, as text rather than a screenshot where you can.
The wiki explains where to find them: https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Troubleshooting#getting-problem-logs
-->

```text

```

## Screenshots

<!--
If applicable, add screenshots to help explain your problem. Redact them first, as described at the top of this form.
-->

## Additional context

<!--
Add any other context about the problem here.
-->
