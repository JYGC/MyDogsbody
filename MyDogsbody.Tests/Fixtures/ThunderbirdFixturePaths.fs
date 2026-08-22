module MyDogsbody.Tests.Fixtures.ThunderbirdFixturePaths

open System.IO

/// Resolves fixture files by the source tree they live in, not by the process working
/// directory - so these paths are stable whether the suite runs from `dotnet test`,
/// `bin/Debug/net9.0`, or an IDE test runner.
let private fixturesRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__))

let measuredShapeProfile = Path.Combine(fixturesRoot, "ThunderbirdProfiles", "measured-shape")
let maildirShapeProfile = Path.Combine(fixturesRoot, "ThunderbirdProfiles", "maildir-shape")
let mboxFixtures = Path.Combine(fixturesRoot, "Mbox")

let mboxFixture (fileName: string) = Path.Combine(mboxFixtures, fileName)
