# Comments to names — tasks

Work in ring order (design.md → Phases). Each phase leaves the solution building with no new
warnings and the suite green before the next phase starts. All tasks are required unless marked
optional.

## Phase 1 — `MyDogsbody.Domain`

- [x] 1.1 Measure `origin/main` before any edit: build 0 errors and 2 warnings, tests 1726/1726
      (design.md → Verification).
- [x] 1.2 Write requirements.md, design.md and this file.
- [x] 1.3 `Documents/`, `Suppliers/`, `MailAccounts/` — apply the sentence rule. Public renames:
      `SupplierMatcher`'s three cases (`SenderAddress`, `SenderDomain`, `SubjectPattern`) and
      `Word`'s two coordinates, with their call sites in `MatchSupplierWorkflow`,
      `PdfDocumentReader` and the tests.
- [x] 1.4 `InvoiceTemplates/` — apply the sentence rule. Public rename: `TemplateRuleShapeInvalid`
      and its call sites in `TemplateApiMappers` and the tests.
- [x] 1.5 `Invoices/` — apply the sentence rule. Public rename: `Money.MaxAbsAmount`. The
      `Money` union gets labelled fields; `selectCandidates` returns a named record.
- [x] 1.6 `Calendar/` — apply the sentence rule. Public renames: `InvoiceSyncKey.PropertyName`
      and `InvoiceSyncKey.parts`, with their call sites in `GoogleCalendarClient`,
      `InvoiceSyncApiMappers` and the tests.
- [x] 1.7 `dotnet build MyDogsbody.sln`: 0 errors and exactly the two baseline warnings.
- [x] 1.8 `dotnet test MyDogsbody.Tests\MyDogsbody.Tests.fsproj`: 1726/1726, 0 skipped.
- [x] 1.9 Re-run the persisted-shape search (design.md) and the classifier over
      `MyDogsbody.Domain`; read the residual.
- [x] 1.10 Write outcome.md: totals, every public rename (old → new), every block kept and why,
      every decision the reader may want to overrule.

## Phases 2–9 — one per ring, in order

Each phase is: apply the sentence rule to the ring's files; update call sites of any public
rename; build; test; re-run the classifier over the ring; add the phase to outcome.md.

- [x] 2. `MyDogsbody.Builders`, `MyDogsbody.Exceptions`, `MyDogsbody.Exceptions.Types`
      (two openers deleted in `ActionNames.fs`; nothing renamed — see outcome.md)
- [x] 3. `MyDogsbody.Database.Models`, `MyDogsbody.Database`, `MyDogsbody.Database.Migrations`
      (a migration is never edited once applied — comments in an applied migration are left
      alone unless the migration has not shipped)
- [x] 4. `MyDogsbody.Integrations.Documents`, `.Google`, `.Thunderbird` and their
      `*.Database.Models`
- [x] 5. `MyDogsbody.Logging`, `MyDogsbody.Logging.Database.Models`
- [x] 6. `MyDogsbody.Startup`
- [x] 7. `MyDogsbody.UI.Types`
- [x] 8. `MyDogsbody.UI.Portal` and the WPF host `MyDogsbody`
- [x] 9. `MyDogsbody.Tests` — Arrange/Act/Assert markers removed (218; reasons kept). Banner
      dividers and descriptive comments **not done**, with the measurements that say why — see
      outcome.md.
- [x] 10. Relocate the rationale comments, leaving a pointer — done on request, for blocks of 10
      lines or more (71 blocks), into `rationale/` in this folder rather than each originating
      change's design.md. Reversible with `restore-relocated-rationale.py`; see outcome.md.
