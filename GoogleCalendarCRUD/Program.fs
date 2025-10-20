open System
open System.IO
open System.Threading
open Google.Apis.Auth.OAuth2
open Google.Apis.Calendar.v3
open Google.Apis.Calendar.v3.Data
open Google.Apis.Services
open Google.Apis.Util.Store

let createService () =
  let scopes = [ CalendarService.Scope.Calendar ]
  let credentialPath = "token.json"

  use stream = new FileStream("credentials.json", FileMode.Open, FileAccess.Read)
  let cred = 
    GoogleWebAuthorizationBroker.AuthorizeAsync(
      GoogleClientSecrets.FromStream(stream).Secrets,
      scopes,
      "user",
      CancellationToken.None,
      FileDataStore(credentialPath, true)
    ).Result

  let serviceInit = BaseClientService.Initializer()
  serviceInit.HttpClientInitializer <- cred
  serviceInit.ApplicationName <- "F# Google Calendar CRUD"
  new CalendarService(serviceInit)

let listEvents (service: CalendarService) calendarId =
  let req = service.Events.List(calendarId)
  req.TimeMinDateTimeOffset <- Nullable(DateTimeOffset.UtcNow.AddDays(-7.0))
  req.TimeMaxDateTimeOffset <- Nullable(DateTimeOffset.UtcNow.AddDays(7.0))
  req.ShowDeleted <- Nullable false
  req.SingleEvents <- Nullable true
  req.OrderBy <- EventsResource.ListRequest.OrderByEnum.StartTime
  let events = req.Execute()
  if events.Items = null || events.Items.Count = 0 then
    printfn "No upcoming events found."
  else
    printfn "Upcoming events:"
    for ev in events.Items do
      let start = if ev.Start.DateTimeDateTimeOffset.HasValue then ev.Start.DateTimeDateTimeOffset.Value else DateTimeOffset.Parse(ev.Start.Date)
      printfn "%s (%O)" ev.Summary start

let createEvent (service: CalendarService) calendarId =
  let newEvent = Event()
  newEvent.Summary <- "F# Test Event"
  newEvent.Location <- "Online"
  newEvent.Description <- "Event created from F#"
  newEvent.Start <- EventDateTime(DateTimeDateTimeOffset = Nullable(DateTimeOffset.UtcNow.AddHours(1.0)), TimeZone = "UTC")
  newEvent.End <- EventDateTime(DateTimeDateTimeOffset = Nullable(DateTimeOffset.UtcNow.AddHours(2.0)), TimeZone = "UTC")

  let created = service.Events.Insert(newEvent, calendarId).Execute()
  printfn "Created event: %s (%s)" created.Summary created.Id
  created.Id

let updateEvent (service: CalendarService) calendarId eventId =
  let ev = service.Events.Get(calendarId, eventId).Execute()
  ev.Summary <- ev.Summary + " (Updated)"
  ev.Description <- "Updated via F#"
  let updated = service.Events.Update(ev, calendarId, ev.Id).Execute()
  printfn "Updated event: %s" updated.Summary

let deleteEvent (service: CalendarService) calendarId eventId =
  service.Events.Delete(calendarId, eventId).Execute()
  |> ignore
  printfn "Deleted event with id %s" eventId

[<EntryPoint>]
let main argv =
  let calendarId =
    if argv.Length > 0 then argv[0]
    else "primary" // default calendar

  let service = createService()

  printfn "1. Listing events"
  listEvents service calendarId

  printfn "\n2. Creating event"
  let eventId = createEvent service calendarId

  printfn "\n3. Updating event"
  updateEvent service calendarId eventId

  printfn "\n4. Deleting event"
  deleteEvent service calendarId eventId

  0
