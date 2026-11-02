# Architecture (Phase 1)

Dependencies point downward only; libraries never reference WinUI.

```
PAIC.App  ->  PAIC.ProjectSystem -> PAIC.Core
          ->  PAIC.Diagnostics
          ->  PAIC.Core
```

- **Dependency injection**: `App.ConfigureServices` registers logging, the project service, recent-projects store, settings store and error reporter with `Microsoft.Extensions.DependencyInjection`.
- **Logging**: `ILogger` everywhere. Providers write JSON lines to `%LocalAppData%\PAIC\logs\paic-yyyyMMdd.log` (14-day retention) and to a bounded in-memory sink shown on the Diagnostics page.
- **Error handling**: user-facing failures are `ProjectException`; the UI funnels exceptions through `IErrorReporter`, which logs them and shows a dialog. Unhandled UI exceptions are logged and reported the same way.
- **Async**: all file operations in the project service are async and accept a `CancellationToken`; operations are serialised by a semaphore.
- **Planned modules** (installer engine, package engine, designer, ...) are added in later phases as separate projects.

## Phase 3 additions
`PAIC.Controls` (element model and contracts, depends on Core and ThemeEngine) is referenced by `PAIC.ProjectSystem`, which stores elements in pages.
`PAIC.Designer` (depends on Controls and ProjectSystem) holds the editing logic with no UI dependency; `PAIC.App/Designer/ElementRenderer` draws the model with WinUI controls.
