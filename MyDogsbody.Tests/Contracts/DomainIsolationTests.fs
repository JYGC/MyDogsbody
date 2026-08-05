module MyDogsbody.Tests.Contracts.DomainIsolationTests

open System
open System.IO
open System.Xml.Linq
open Xunit

/// MyDogsbody.Domain referencing nothing is the invariant the whole architecture rests on -
/// break it and the domain stops being testable with plain values. The build enforces it too
/// (see the AssertDomainReferencesNothing target), but a reference added with the build
/// temporarily disabled would slip through, so it is asserted here as well.
///
/// Walks up from the test assembly rather than hard-coding a path, so this keeps working
/// whatever the working directory is.
let private repositoryRoot () =
    let rec find (directory: DirectoryInfo) =
        if isNull (box directory) then
            failwith "Could not locate MyDogsbody.sln above the test assembly."
        elif File.Exists(Path.Combine(directory.FullName, "MyDogsbody.sln")) then
            directory.FullName
        else
            find directory.Parent

    find (DirectoryInfo(AppContext.BaseDirectory))

let private domainProjectFile () =
    Path.Combine(repositoryRoot (), "MyDogsbody.Domain", "MyDogsbody.Domain.fsproj")

[<Fact; Trait("Level", "Contract")>]
let ``MyDogsbody.Domain declares no ProjectReference`` () =
    // Arrange
    let projectFile = domainProjectFile ()
    Assert.True(File.Exists projectFile, $"Expected the domain project at {projectFile}")

    // Act
    let references =
        XDocument.Load(projectFile).Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName = "ProjectReference")
        |> Seq.map (fun element -> element.Attribute(XName.Get "Include").Value)
        |> Seq.toList

    // Assert
    Assert.True(
        List.isEmpty references,
        "MyDogsbody.Domain must reference no other project, but found: "
        + String.Join(", ", references)
        + ". Declare a dependency function type instead."
    )

[<Fact; Trait("Level", "Contract")>]
let ``MyDogsbody.Domain declares no PackageReference`` () =
    // Arrange - FSharp.Core is supplied implicitly by the SDK and never appears in the file.
    let projectFile = domainProjectFile ()

    // Act
    let packages =
        XDocument.Load(projectFile).Descendants()
        |> Seq.filter (fun element -> element.Name.LocalName = "PackageReference")
        |> Seq.map (fun element -> element.Attribute(XName.Get "Include").Value)
        |> Seq.toList

    // Assert
    Assert.True(
        List.isEmpty packages,
        "MyDogsbody.Domain must reference no package, but found: "
        + String.Join(", ", packages)
    )

[<Fact; Trait("Level", "Contract")>]
let ``the domain assembly references no MyDogsbody assembly`` () =
    // Arrange - the .fsproj assertions above cover what is declared; this covers what was
    // actually linked, which is what a workflow can really reach at runtime.
    let domainAssembly = typeof<MyDogsbody.Domain.Credentials.CredentialError>.Assembly

    // Act
    let myDogsbodyReferences =
        domainAssembly.GetReferencedAssemblies()
        |> Array.map (fun assemblyName -> assemblyName.Name)
        |> Array.filter (fun name ->
            not (isNull name) && name.StartsWith("MyDogsbody", StringComparison.Ordinal))
        |> Array.toList

    // Assert
    Assert.True(
        List.isEmpty myDogsbodyReferences,
        "The domain assembly must link no MyDogsbody assembly, but found: "
        + String.Join(", ", myDogsbodyReferences)
    )
