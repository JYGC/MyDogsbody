module MyDogsbody.Tests.Infrastructure.DocumentInfrastructure.GetContentSplitByLinesTests

open System
open Xunit
open MyDogsbody.Domains.DocumentDomain
open MyDogsbody.Exceptions.Types
open MyDogsbody.Domains.Types.DocumentTypes
open MyDogsbody.Builders

// Minimal stub for HandleErrorBuilder
type TestHandleErrorBuilder() =
  member _.Bind(x, f) = Result.bind f x
  member _.Return(x) = Ok x
  member _.ReturnFrom(x) = x

let handleError = HandleErrorBuilder (fun _ -> ())

// Stub DocumentObject with a simple word list
let fakeDocumentObject : DocumentHandler =
  { GetWords = fun () ->
      [ { Text = "Hello"; Left = 10.0; Bottom = 100.0 }
        { Text = "World"; Left = 50.0; Bottom = 100.0 } ] }

[<Fact>]
let ``getContentSplitByLines returns Error when exception occurs`` (): unit =
  // Act
  let result = getContentSplitByLines handleError fakeDocumentObject

  // Assert
  match result with
  | Error ex ->
      Assert.IsType<MyDogsbodyException>(ex)
      Assert.Contains("Failed to extract content from PDF", ex.Message)
      Assert.NotNull(ex.InnerException)
      Assert.IsType<DivideByZeroException>(ex.InnerException) |> ignore
  | Ok lines ->
      Assert.Fail($"Expected Error but got Ok with {lines.Length} lines")