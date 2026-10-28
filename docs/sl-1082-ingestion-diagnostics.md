# SL-1082 ingestion investigation

The deployed stack (`InvalidDataException` in `ReadValidatedBiaCoreAsync` / `ReadValidatedBiaAsync`) does not identify the validation branch. No root cause or successful recovery is claimed by this change. Guards, proxy rules, robots enforcement, selectors and source state remain unchanged.

Both readers now emit a Warning through their own logger category, with exception type, at most eight method frames (1200 characters) and a fixed reason. They never pass an exception object, message, inner exception, file path, HTTP content or product data to the logger. Expected access blocks and cancellation retain their existing handling without a diagnostic warning. Job error codes/summaries retain their existing sanitized contract.

| Reason | Validation branch |
| --- | --- |
| RobotsPolicyDenied | Existing robots policy check denied the category |
| ProductCountInvalid | Existing selector returned outside its bounded count |
| ProductSchemaInvalid | Required card identity/name/price/link failed validation |
| ProductUrlDenied | Product URL failed the existing host/path guard |
| EmptyCatalog | BHX result is empty after validation |
| Unclassified | Other exceptions; use exception type and safe method frames |

After rollout, collect the reason and exception type from each reader category for the same fresh sync-job window. A count failure alone does not establish why the DOM differs; do not change selectors or bypass access policy without further evidence. For Unclassified failures, safe frames may still require a more narrowly instrumented follow-up. Warning level makes these diagnostics visible with the default Information minimum.

## Polling and source scheduling

`BhxIngestionWorker.ExecuteAsync` waits 5–59 seconds at startup, runs `RunDueSources`, and checks again via `PeriodicTimer`. `Ingestion:PollIntervalMinutes` defaults to 60; the reported deployment sets 5. A poll need not create a job.

`EfSyncJobStore.DueSources` requires Active state and either a queued retry, an expired running lease, or elapsed source cadence plus next-attempt time. An unexpired running lease prevents overlap. `TryClaim` repeats eligibility under a serializable transaction.

On ordinary failure, `Complete` calls `ScheduleFailureRetry`: next attempt is six hours after finish, without setting LastCompletedAtUtc. For the reported jobs finishing around 2026-10-28 11:00Z, the earliest ordinary retry is around 17:00Z (assuming no prior success cadence, queued retry or state change). Thus one job per source by 14:40Z is consistent with the code, even with five-minute polls. Successful/partial completion sets a 72-hour cadence; blocked sources pause until explicit intervention. An admin queued retry can bypass the ordinary time gates but cannot bypass Active state or a live lease.

Regression coverage checks persisted eligibility at the five-minute poll, just before and at the six-hour retry boundary, during the new lease, and at the subsequent 72-hour success boundary. Existing tests cover configured poll intervals, expired leases and paused sources. No scheduling defect has been established, and scheduling is unchanged.

Runtime acceptance remains separate: fresh successful BHX and CellphoneS ingestion (CellphoneS importedCount > 0), public catalog verification, and Worker survival through a poll interval under the deployed configuration. SL-1082 remains open until those gates pass.
