# Sent Email Import

This folder contains the implementation of the **Sent Email Import** feature for Microsoft 365 Analytics & Insights.

## Overview

`SentEmailImporter` reads sent items from each user's mailbox via Microsoft Graph and stores one row per
message in the `SentEmails` table, with each recipient stored as a row in the `SentEmailRecipients`
join table. Optional sentiment scoring is performed via Azure AI Language (Text Analytics) when a
Cognitive Services configuration is present.

For each user with an email address in the analytics database, the importer:

1. Loads the user's `sentitems` mail folder using the Graph delta query
   (`/users/{upn}/mailFolders/sentitems/messages/delta`).
2. Persists the delta token via `IDeltaTokenStore` so subsequent runs only fetch new messages.
3. Inserts one `SentEmails` row per message and one `SentEmailRecipients` row per distinct recipient.
4. Optionally calls Azure AI Language to compute a positive sentiment score for the message body.

Mailboxes that cannot be accessed (for example, unlicensed users or mailboxes restricted by
ApplicationAccessPolicy) are logged as warnings and skipped; the import continues for the remaining
users.

## Required Graph permissions

This feature requires an additional **application** permission beyond the base solution permissions.
Admin consent is required.

| API   | Permission | Reason                                                                 |
|-------|------------|------------------------------------------------------------------------|
| Graph | Mail.Read  | Read messages from each user's `sentitems` folder via the delta query. |

> **Note:** `Mail.Read` grants the runtime app registration read access to **all** mailboxes in the
> tenant. To limit which mailboxes the importer can read, configure an
> [Application Access Policy](https://learn.microsoft.com/graph/auth-limit-mailbox-access) in
> Exchange Online and scope it to the runtime service principal.

The base solution permissions (e.g. `User.Read.All`, `Reports.Read.All`, etc.) are documented in the
[Prerequisites](https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Prerequisites) wiki page.

## Configuration

The importer is invoked from the Office 365 activity importer web job when sent-email import is
enabled. Relevant settings:

- **Cognitive Services (optional)** – Set `CognitiveEndpoint` and `CognitiveKey` in app settings to
  enable sentiment scoring (`SentEmail.CognitiveScore`). When not configured, messages are imported
  without a sentiment score.
- **Delta tokens** – Stored per user (`SentEmails-{userPrincipalName}`) via `IDeltaTokenStore`. To
  force a full re-scan for a user, clear that user's delta token.

## Data model

A sent message is normalised across two tables so the message-level fields are stored once,
regardless of how many recipients the message had.

### `sent_emails` (one row per message)

| Column          | Description                                                  |
|-----------------|--------------------------------------------------------------|
| GraphMessageId  | Graph message ID (unique).                                   |
| Subject         | Message subject (truncated to 1000 characters).              |
| SentDate        | `sentDateTime` from Graph.                                   |
| FromAddressID   | FK to `EmailAddresses` for the sender.                       |
| UserID          | FK to the sending `User`.                                    |
| CognitiveScore  | Optional positive-sentiment score (0.0–1.0).                 |

### `sent_email_recipients` (one row per recipient of each message)

| Column             | Description                                          |
|--------------------|------------------------------------------------------|
| SentEmailID        | FK to `sent_emails` (cascade delete).                |
| RecipientAddressID | FK to `EmailAddresses` for the recipient.            |

A unique index on `(sent_email_id, recipient_address_id)` prevents the same recipient from being
recorded twice for the same message.

### `email_addresses`

Lookup table for sender and recipient email addresses, shared by `from_address_id` on `sent_emails`
and `recipient_address_id` on `sent_email_recipients`.

## Operational notes

- Messages already present in `SentEmails` (matched by `GraphMessageId`) are skipped, so reruns are
  idempotent.
- HTML bodies are stripped to plain text before being sent to Azure AI Language.
- Errors against an individual mailbox do not abort the overall import.
