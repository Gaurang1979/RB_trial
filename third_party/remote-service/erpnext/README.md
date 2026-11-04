# remote_service (ERPNext app)

Receives finished remote service sessions from the relay. For each session it makes a **Remote Service
Log** (machine, customer, engineer, Employee, reason, start, end, every action). It also makes a
**draft Timesheet** for the engineer, who adds a work note and submits it.

## Install

```bash
bench get-app remote_service <path or repository>/erpnext
```

```bash
bench --site <site> install-app remote_service
```

## Set up

1. Make an integration user (no desk access) with the role **Remote Service Integration**.
2. Generate an API key and secret for that user, and give them to the relay as `ERPNEXT_API_KEY` and
   `ERPNEXT_API_SECRET`.
3. In **Remote Service Settings**, choose the Activity Type for remote service time. Untick "Create
   Draft Timesheet" for logs only.
4. On the relay, give each engineer account its ERPNext Employee: `add-user … --employee HR-EMP-0007`.

## How a session is matched

- **Customer:** found from the machine's licence key when the `vision_licence` app is installed.
- **Timesheet:** made only when the Employee exists. Otherwise the log says why in "Timesheet Note".
- **Repeats:** a session sent twice makes one log. The session id is the log's name.
