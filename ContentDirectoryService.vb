Imports System.Text
Imports System.Xml

Partial Public Class Plugin
    <UpnpServiceVariable("A_ARG_TYPE_BrowseFlag", "string", False, "BrowseMetadata", "BrowseDirectChildren")> _
    <UpnpServiceVariable("ContainerUpdateIDs", "string", True)> _
    <UpnpServiceVariable("SystemUpdateID", "ui4", True)> _
    <UpnpServiceVariable("A_ARG_TYPE_Count", "ui4", False)> _
    <UpnpServiceVariable("A_ARG_TYPE_SortCriteria", "string", False)> _
    <UpnpServiceVariable("A_ARG_TYPE_SearchCriteria", "string", False)> _
    <UpnpServiceVariable("SortCapabilities", "string", False)> _
    <UpnpServiceVariable("A_ARG_TYPE_Index", "ui4", False)> _
    <UpnpServiceVariable("A_ARG_TYPE_ObjectID", "string", False)> _
    <UpnpServiceVariable("A_ARG_TYPE_UpdateID", "ui4", False)> _
    <UpnpServiceVariable("A_ARG_TYPE_Result", "string", False)> _
    <UpnpServiceVariable("SearchCapabilities", "string", False)> _
    <UpnpServiceVariable("A_ARG_TYPE_Filter", "string", False)> _
    <UpnpServiceVariable("A_ARG_TYPE_Featurelist", "string", False)> _
    Friend NotInheritable Class ContentDirectoryService
        Inherits UpnpService

        ' UPnP cache invalidation. Per ContentDirectory:1 spec:
        '   * SystemUpdateID MUST monotonically increase whenever any container
        '     changes (track added/removed/edited, settings changed, etc.).
        '   * The per-Browse UpdateID slot tells clients "your cached DIDL for
        '     this container is valid only as long as this number doesn't change".
        ' Returning a constant "0" (which this plugin did until 2026-05-28) tells
        ' clients "nothing ever changes, use your cache forever". BubbleUPnP and
        ' other strict clients then serve stale DIDL across MB restarts until
        ' some opaque trigger forces a re-Browse - typically requiring a second
        ' MB restart to see fresh content.
        '
        ' Strategy:
        '   1. Initialise at plugin load to seconds-since-epoch (masked into the
        '      positive Integer range) so every restart starts strictly ahead of
        '      any prior session. Fixes cross-restart staleness even when nothing
        '      else changed.
        '   2. BumpSystemUpdateId() is called from ItemManager.SetLibraryDirty -
        '      which is already wired to MB's FileAdded/FileDeleted/TagsChanged
        '      notifications - and from anywhere else the browse tree's shape
        '      shifts (settings save, view-binding change). Per-container UpdateID
        '      precision isn't achievable (a single tag edit can shift a file into
        '      a different artist/album/genre/folder bucket, so any container
        '      grouping on those fields is potentially affected); the global-bump
        '      pattern is what Plex, MinimServer, Jellyfin, and BubbleUPnP Server
        '      itself all do.
        '   3. Emitted from GetSystemUpdateID, the GENA event-property payload
        '      (for clients subscribing fresh), and the UpdateID slot of every
        '      Browse / Search response.
        Private Shared systemUpdateId As Integer = CInt((DateTime.UtcNow.Ticks \ TimeSpan.TicksPerSecond) And &H7FFFFFFFL)

        Friend Shared Function GetCurrentSystemUpdateId() As String
            Return systemUpdateId.ToString()
        End Function

        Friend Shared Sub BumpSystemUpdateId()
            Threading.Interlocked.Increment(systemUpdateId)
        End Sub

        ' Default search scope when the client sends ContainerID="0" (which BubbleUPnP
        ' always does regardless of UI position - see history note in FIXES.md).
        '
        ' Per-client navigation tracking was tried first but BubbleUPnP's client-side
        ' Browse cache hides most navigation from us (cached Browses never reach the
        ' server), so the "last seen container" anchor goes stale within seconds. Net
        ' result: scoped searches were unreliable and often surprised the user with
        ' results from a stale location.
        '
        ' Defaulting all global searches into "L:music" instead is the saner choice:
        '   • Eliminates noise from podcast/audiobook/radio/inbox tag pollution
        '     (searching "history" otherwise returns 4600 podcast episodes).
        '   • Predictable - same scope regardless of where the user was.
        '   • Cheap - no state, no cache, no IP tracking.
        ' If we later want podcast/audiobook search, expose it as a separate Search
        ' container ID the client picks explicitly (e.g. a top-level "Search Podcasts"
        ' shortcut) rather than guessing at user intent.
        Friend Shared Function ResolveSearchContainer(request As HttpRequest, requestedContainer As String) As String
            If requestedContainer = "0" Then Return "L:music"
            Return requestedContainer
        End Function

        Public Sub New(server As UpnpServer)
            MyBase.New(server, "urn:schemas-upnp-org:service:ContentDirectory:1", "urn:upnp-org:serviceId:ContentDirectory", "/ContentDirectory.control", "/ContentDirectory.event", "/ContentDirectory.xml")
        End Sub

        Protected Overrides Sub WriteEventProperty(writer As XmlWriter)
            writer.WriteStartElement("e", "property", Nothing)
            writer.WriteElementString("SystemUpdateID", GetCurrentSystemUpdateId())
            writer.WriteEndElement()
        End Sub

        <UpnpServiceArgument(0, "SearchCaps", "SearchCapabilities")> _
        Private Sub GetSearchCapabilities(request As HttpRequest)
            ' Returning empty here advertises "Search is unsupported" to UPnP clients -
            ' BubbleUPnP honours this by showing "Library doesn't support search" rather
            ' than even attempting a Search action. ItemManager.Search DOES implement
            ' search (handles the canonical "(upnp:class derivedfrom ...) and dc:title
            ' contains ..." query that BubbleUPnP and other clients issue), so advertise
            ' the matching property set:
            '   dc:title    - the search term itself (matched by ItemManager.Search)
            '   upnp:class  - used by clients to scope to musicAlbum vs musicTrack
            '   upnp:artist, upnp:album, upnp:genre - common scopes clients may add
            '   dc:creator  - alternate-name alias some clients use for artist
            request.Response.SendSoapHeadersBody(request, "dc:title,upnp:class,upnp:artist,upnp:album,upnp:genre,dc:creator")
        End Sub

        <UpnpServiceArgument(0, "SortCaps", "SortCapabilities")> _
        Private Sub GetSortCapabilities(request As HttpRequest)
            'dc:title,upnp:album,dc:creator,upnp:artist,upnp:albumArtist,upnp:genre
            request.Response.SendSoapHeadersBody(request, "")
        End Sub

        <UpnpServiceArgument(0, "Id", "SystemUpdateID")> _
        Private Sub GetSystemUpdateID(request As HttpRequest)
            request.Response.SendSoapHeadersBody(request, GetCurrentSystemUpdateId())
        End Sub

        <UpnpServiceArgument(0, "Result", "A_ARG_TYPE_Result")> _
        <UpnpServiceArgument(1, "NumberReturned", "A_ARG_TYPE_Count")> _
        <UpnpServiceArgument(2, "TotalMatches", "A_ARG_TYPE_Count")> _
        <UpnpServiceArgument(3, "UpdateID", "A_ARG_TYPE_UpdateID")> _
        Private Sub Browse(request As HttpRequest, _
                <UpnpServiceArgument("A_ARG_TYPE_ObjectID")> ObjectID As String, _
                <UpnpServiceArgument("A_ARG_TYPE_BrowseFlag")> BrowseFlag As String, _
                <UpnpServiceArgument("A_ARG_TYPE_Filter")> Filter As String, _
                <UpnpServiceArgument("A_ARG_TYPE_Index")> StartingIndex As String, _
                <UpnpServiceArgument("A_ARG_TYPE_Count")> RequestedCount As String, _
                <UpnpServiceArgument("A_ARG_TYPE_SortCriteria")> SortCriteria As String)
            Dim result As String = Nothing
            Dim numberReturned As String = Nothing
            Dim totalMatches As String = Nothing
            Dim startIndexValue As UInteger
            Dim requestCountValue As UInteger
            Dim browseFlagValue As BrowseFlag
            If Not UInteger.TryParse(StartingIndex, startIndexValue) OrElse Not UInteger.TryParse(RequestedCount, requestCountValue) OrElse Not [Enum].TryParse(BrowseFlag, True, browseFlagValue) Then
                LogInformation("RegisterDevice", "Invalid Browse Args:" & StartingIndex & "," & RequestedCount & "," & BrowseFlag.ToString())
                Throw New SoapException(402, "Invalid Args")
            End If
            Dim directory As ItemManager = ItemManager.GetItemManager(request.Headers)
            Try
                directory.Browse(request.Headers, ObjectID, browseFlagValue, Filter, CInt(startIndexValue), If(requestCountValue > Integer.MaxValue, Integer.MaxValue, CInt(requestCountValue)), SortCriteria, result, numberReturned, totalMatches)
            Catch ex As Exception
                ' F50 - surface the actual cause + ALL the failing browse parameters in the log instead
                ' of letting the outer SOAP-fault wrapper hide it as a generic "Action Failed". Includes:
                ' ObjectID (the container), BrowseFlag (metadata vs children), Filter (which attrs
                ' the client wanted), pagination (Index/Count), Sort criteria, partial result length.
                LogError(ex, "Browse", "ObjectID=" & ObjectID _
                    & " BrowseFlag=" & BrowseFlag _
                    & " Filter=" & Filter _
                    & " startingIndex=" & startIndexValue _
                    & " requestedCount=" & requestCountValue _
                    & " sortCriteria=" & SortCriteria _
                    & " partialResultLength=" & If(result Is Nothing, "0", result.Length.ToString()) _
                    & Environment.NewLine & ex.StackTrace)
                Throw
            End Try
            request.Response.SendSoapHeadersBody(request, result, numberReturned, totalMatches, GetCurrentSystemUpdateId())
        End Sub

        <UpnpServiceArgument(0, "Result", "A_ARG_TYPE_Result")> _
        <UpnpServiceArgument(1, "NumberReturned", "A_ARG_TYPE_Count")> _
        <UpnpServiceArgument(2, "TotalMatches", "A_ARG_TYPE_Count")> _
        <UpnpServiceArgument(3, "UpdateID", "A_ARG_TYPE_UpdateID")> _
        Private Sub Search(request As HttpRequest, _
                <UpnpServiceArgument("A_ARG_TYPE_ObjectID")> ContainerID As String, _
                <UpnpServiceArgument("A_ARG_TYPE_SearchCriteria")> SearchCriteria As String, _
                <UpnpServiceArgument("A_ARG_TYPE_Filter")> Filter As String, _
                <UpnpServiceArgument("A_ARG_TYPE_Index")> StartingIndex As String, _
                <UpnpServiceArgument("A_ARG_TYPE_Count")> RequestedCount As String, _
                <UpnpServiceArgument("A_ARG_TYPE_SortCriteria")> SortCriteria As String)
            Dim startIndexValue As UInteger
            Dim requestCountValue As UInteger
            If Not UInteger.TryParse(StartingIndex, startIndexValue) OrElse Not UInteger.TryParse(RequestedCount, requestCountValue) Then
                LogInformation("Search", "Invalid Args: " & StartingIndex & "," & RequestedCount)
                Throw New SoapException(402, "Invalid Args")
            End If
            Dim directory As ItemManager = ItemManager.GetItemManager(request.Headers)
            Dim result As String = Nothing
            Dim numberReturned As String = Nothing
            Dim totalMatches As String = Nothing
            ' Substitute "0" (global from root) with the default search scope
            ' ("L:music" today) - see ResolveSearchContainer for rationale.
            Dim effectiveContainer As String = ResolveSearchContainer(request, ContainerID)
            ' Log BOTH the requested and resolved container IDs so it's obvious in the
            ' log whether BubbleUPnP sent a scoped ID or whether we substituted. Without
            ' this, the downstream ItemManager.Search log only shows the resolved value
            ' and "object=L:music" can be confused for "BubbleUPnP scoped to music" when
            ' really BubbleUPnP sent "0" and we substituted.
            If ContainerID <> effectiveContainer Then
                LogInformation("Search", "client containerID=" & ContainerID & " resolved=" & effectiveContainer)
            End If
            directory.Search(request.Headers, effectiveContainer, SearchCriteria, Filter, CInt(startIndexValue), If(requestCountValue > Integer.MaxValue, Integer.MaxValue, CInt(requestCountValue)), SortCriteria, result, numberReturned, totalMatches)
            request.Response.SendSoapHeadersBody(request, result, numberReturned, totalMatches, GetCurrentSystemUpdateId())
        End Sub

        <UpnpServiceArgument(0, "FeatureList", "A_ARG_TYPE_Featurelist")> _
        Private Sub X_GetFeatureList(request As HttpRequest)
            Dim text As New StringBuilder(1024)
            Dim xmlSettings As New XmlWriterSettings With {
                .OmitXmlDeclaration = True
            }
            Using writer As XmlWriter = XmlWriter.Create(text, xmlSettings)
                writer.WriteStartElement("Features", "urn:schemas-upnp-org:av:avs")
                writer.WriteAttributeString("xmlns", "xsi", Nothing, "http://www.w3.org/2001/XMLSchema-instance")
                writer.WriteAttributeString("xsi", "schemaLocation", Nothing, "urn:schemas-upnp-org:av:avs http://www.upnp.org/schemas/av/avs.xsd")
                writer.WriteStartElement("Feature")
                writer.WriteAttributeString("name", "samsung.com_BASICVIEW")
                writer.WriteAttributeString("version", "1")
                writer.WriteStartElement("container")
                writer.WriteAttributeString("id", "1")
                writer.WriteAttributeString("type", "object.item.audioItem")
                writer.WriteEndElement()
                writer.WriteStartElement("container")
                writer.WriteAttributeString("id", "2")
                writer.WriteAttributeString("type", "object.item.videoItem")
                writer.WriteEndElement()
                writer.WriteStartElement("container")
                writer.WriteAttributeString("id", "3")
                writer.WriteAttributeString("type", "object.item.imageItem")
                writer.WriteEndElement()
                writer.WriteEndElement()
                writer.WriteEndElement()
            End Using
            request.Response.SendSoapHeadersBody(request, text.ToString())
        End Sub
    End Class  ' ContentDirectoryService

    Friend Enum BrowseFlag
        BrowseMetadata
        BrowseDirectChildren
    End Enum  ' BrowseFlag
End Class
