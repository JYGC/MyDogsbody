# Rationale moved out of the source

Phase 10 of `comments-to-names`. Every comment block of 10 lines or more was
moved verbatim into the file for its project below, and replaced in the source by a one-line
pointer of the form `Rationale: <this folder>/<Project>.md - <File>.fs: <symbol>`.

Not moved: banner dividers, a module's own doc comment, blocks in applied migrations, and
blocks on a symbol CLAUDE.md or CLAUDE-project.md cites by name.

| Project | Blocks | Lines |
| --- | ---: | ---: |
| [`MyDogsbody.Database`](MyDogsbody.Database.md) | 2 | 41 |
| [`MyDogsbody.Domain`](MyDogsbody.Domain.md) | 28 | 447 |
| [`MyDogsbody.Integrations.Google`](MyDogsbody.Integrations.Google.md) | 4 | 46 |
| [`MyDogsbody.Integrations.Thunderbird`](MyDogsbody.Integrations.Thunderbird.md) | 7 | 103 |
| [`MyDogsbody.Startup`](MyDogsbody.Startup.md) | 11 | 167 |
| [`MyDogsbody.Tests`](MyDogsbody.Tests.md) | 16 | 214 |
| [`MyDogsbody.UI.Portal`](MyDogsbody.UI.Portal.md) | 2 | 21 |
| [`MyDogsbody.UI.Types`](MyDogsbody.UI.Types.md) | 1 | 10 |
| **Total** | **71** | **1049** |
