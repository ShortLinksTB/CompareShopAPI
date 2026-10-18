# SL-912 changed-line coverage

Source report: [`coverage.cobertura.xml`](coverage.cobertura.xml), collected by `dotnet test CompareShopAPI.slnx --collect:"XPlat Code Coverage"` (65 passed, 0 failed). The report records 92.44% overall line coverage.

Changed-line calculation compares added C# lines in the PR diff (`origin/dev...HEAD`) with executable line entries in the Cobertura report. Generated migration/designer/snapshot files and test files are excluded. Non-executable changed lines are not counted.

| Changed production file | Covered / executable changed lines | Coverage |
| --- | ---: | ---: |
| `CompareShopAPI.Api/Articles/ArticleEndpoints.cs` | 39 / 47 | 83.0% |
| `CompareShopAPI.Api/Program.cs` | 7 / 7 | 100.0% |
| `CompareShopAPI.Application/Articles/ArticleContracts.cs` | 70 / 70 | 100.0% |
| `CompareShopAPI.Domain/Articles/Article.cs` | 107 / 111 | 96.4% |
| `CompareShopAPI.Infrastructure/Articles/AdminArticlePermissionGate.cs` | 2 / 2 | 100.0% |
| `CompareShopAPI.Infrastructure/Articles/EfArticleRepository.cs` | 62 / 64 | 96.9% |
| `CompareShopAPI.Infrastructure/Articles/UserCommerceArticleProductLookup.cs` | 2 / 2 | 100.0% |
| `CompareShopAPI.Infrastructure/Persistence/CompareShopDbContext.cs` | 24 / 25 | 96.0% |
| `CompareShopAPI.Workers/Articles/ArticlePublishingWorker.cs` | 17 / 18 | 94.4% |
| **Total** | **330 / 346** | **95.4%** |

Cobertura class line entries are aggregated by source file and line number before matching, so duplicate class entries do not double-count lines.
