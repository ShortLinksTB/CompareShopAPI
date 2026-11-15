## SL-2329 coverage evidence

Measured from the full `dotnet test CompareShopAPI.slnx -c Release --settings coverage.runsettings --collect:"XPlat Code Coverage"` run on the changes in this PR, using the Cobertura report in this directory and `origin/dev` (`5e2d4f0f31bb3ecf4e67eeccd9cf0131110438d3`) as the comparison base.

- Full suite: **306 passed, 0 failed, 0 skipped**.
- Changed executable production lines: **274/287 (95.47%)**.
- `AdminEndpoints.MapSyncJobEndpoints`: **24/24 lines (100%)**.
- Focused Sync Jobs and catalog diagnostic tests: **51 passed, 0 failed**.

The endpoint tests cover legacy and paged list responses, invalid status/page handling, logs success/404/403 responses, page-size capping, and Retry 202/403/404 behavior. Reader tests verify stage callbacks from both catalog readers; coordinator tests verify the failed stage and persisted duration. Repeated reader stages are aggregated by step to keep normal log volume bounded.
