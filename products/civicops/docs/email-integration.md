# Email integration

The public cut defaults to CIVICOPS_EMAIL_MODE=outbox. A report or status change with a valid email recipient writes an inspectable .eml file under email-outbox.

SMTP is opt-in. Set the mode to smtp and provide the host, port, user, password, sender, and TLS setting through environment variables. No credential is stored in the repository.

GET /api/email/status reports only the active mode, SMTP configuration state, and whether a recipient is configured. It does not return sender or recipient addresses. No phone-based messaging channel is included.
