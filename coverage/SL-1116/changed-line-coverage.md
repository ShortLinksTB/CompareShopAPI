Changed-line production coverage against dev `f05129b`: **38/39 (97.44%)**.

184 tests passed, zero failed/skipped. Full suite uses isolated Testcontainers.MsSql.

| File | Covered | Changed executable lines |
|---|---:|---:|
| `CompareShopAPI.Application/Ingestion/IngestionContracts.cs` | 1 | 2 |
| `CompareShopAPI.Workers/Ingestion/BhxPlaywrightCatalogReader.cs` | 18 | 18 |
| `CompareShopAPI.Workers/Ingestion/CatalogReadDiagnostics.cs` | 6 | 6 |
| `CompareShopAPI.Workers/Ingestion/CellphoneSPlaywrightCatalogReader.cs` | 13 | 13 |

Method: intersect new-side `git diff --unified=0` line ranges with Cobertura production line records; deduplicate state-machine/class records by filename and line, retaining maximum hits. No production exclusions. Tests and documentation are outside production coverage. Raw report: `coverage.cobertura.xml`.
