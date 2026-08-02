Imports System.Text
Imports System.Xml
Imports System.Net

' F2.01 - MusicBee as a UPnP MediaRenderer.
'
' MusicBee advertises an EMBEDDED MediaRenderer device on the SAME UpnpServer/HttpServer/SSDP
' as the MediaServer (no second port). A control point (e.g. BubbleUPnP) can select MusicBee
' as a renderer and drive playback: Play/Pause/Stop/Seek/Next/Previous + volume.
'
' SetAVTransportURI takes one of two paths:
'   - LOOPBACK (Phase 1): the URI points at our OWN HTTP server (a /Files/ or /Encode/ track URL),
'     so we decode the track id and play the LOCAL library file directly via the MusicBee API -
'     bit-perfect, instant, no HTTP round-trip to ourselves.
'   - REMOTE (Phase 2): any other absolute http/https URI - a file served by the controller's own
'     phone (Symfonium), a NAS, another MediaServer. We hand the URL straight to MusicBee's player
'     (NowPlayingList_PlayNow), the same path it uses for internet radio, and let MusicBee stream it.
'     Title/duration come from the controller-supplied DIDL CurrentURIMetaData, since MusicBee
'     reports nothing useful for a URL that isn't in its library.
' Anything else (file://, rtsp://, a relative string) is REFUSED with AVTransport error 716 rather
' than accepted-then-silently-ignored - otherwise the controller thinks the track loaded, sends Play,
' and MusicBee resumes some unrelated stale entry ("source file could not be found").
'
' EVENTING: the UpnpService base sends a LastChange event once at subscribe time and does not
' retain subscribers for live push. Control points poll GetTransportInfo/GetPositionInfo, so
' transport state + position display work. Live push of externally-driven state changes is Phase 3.

Partial Public Class Plugin

    ' Maps the incoming AVTransport URI to a local MusicBee library file path, but ONLY when the
    ' URI is one of our own server's track URLs. Returns Nothing for anything else (Phase 2 territory).
    Friend Shared Function ResolveLoopbackFile(uri As String) As String
        If String.IsNullOrEmpty(uri) Then Return Nothing
        Try
            Dim parsed As New Uri(uri)
            ' Only our own server. boundServerPort is the port the HTTP server actually bound (F3.19).
            If parsed.Port <> boundServerPort Then Return Nothing
            Dim segments() As String = parsed.AbsolutePath.Split(New Char() {"/"c}, StringSplitOptions.RemoveEmptyEntries)
            If segments.Length < 2 Then Return Nothing
            Dim folder As String = segments(0).ToLowerInvariant()
            If folder <> "files" AndAlso folder <> "encode" Then Return Nothing
            Dim filename As String = segments(segments.Length - 1)
            If filename.Length < 16 Then Return Nothing
            ' Same id scheme MediaServerDevice.GetFile / GetEncodedFile use: first 16 chars = object id.
            Dim id As String = filename.Substring(0, 16)
            Dim manager As ItemManager = ItemManager.GetItemManager(New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase))
            Dim localUrl As String = Nothing
            Dim duration As TimeSpan
            If manager.TryGetFileInfo(id, localUrl, duration) Then
                Return localUrl
            End If
        Catch ex As Exception
            LogError(ex, "ResolveLoopbackFile", "uri=" & uri)
        End Try
        Return Nothing
    End Function

    ' Phase 2 - a source we do NOT host. Accepts any absolute http/https URI and returns it
    ' unchanged (never re-serialized: re-encoding an already-escaped path is how casting URLs get
    ' corrupted). Everything else - file:// on a foreign machine, rtsp://, a relative string -
    ' returns Nothing, which the caller turns into a proper UPnP error.
    ' (Parameter deliberately not named "uri": VB is case-insensitive, so it would shadow the Uri type.)
    Friend Shared Function ResolveRemoteUri(value As String) As String
        If String.IsNullOrEmpty(value) Then Return Nothing
        Dim trimmed As String = value.Trim()
        Dim parsed As Uri = Nothing
        If Not Uri.TryCreate(trimmed, UriKind.Absolute, parsed) Then Return Nothing
        If parsed.Scheme <> Uri.UriSchemeHttp AndAlso parsed.Scheme <> Uri.UriSchemeHttps Then Return Nothing
        Return trimmed
    End Function

    ' Pulls the display title + duration out of the controller-supplied DIDL-Lite metadata.
    ' Needed only for the remote path: MusicBee knows nothing about a URL that isn't in its library,
    ' so NowPlaying_GetDuration returns 0 and the controller's progress bar would sit at zero.
    ' The SOAP layer hands us InnerXml (see UpnpService.ProceedControl), so the DIDL arrives
    ' entity-escaped - decode before parsing. Best-effort: bad or absent metadata is not an error.
    Friend Shared Sub ParseDidlMetadata(metadata As String, ByRef title As String, ByRef durationMs As Integer)
        title = Nothing
        durationMs = 0
        If String.IsNullOrEmpty(metadata) Then Exit Sub
        Try
            Dim xml As String = WebUtility.HtmlDecode(metadata).Trim()
            If Not xml.StartsWith("<") Then Exit Sub
            Dim document As New XmlDocument()
            document.LoadXml(xml)
            Dim namespaceManager As New XmlNamespaceManager(document.NameTable)
            namespaceManager.AddNamespace("didl", "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/")
            namespaceManager.AddNamespace("dc", "http://purl.org/dc/elements/1.1/")
            Dim titleNode As XmlNode = document.SelectSingleNode("//dc:title", namespaceManager)
            If titleNode IsNot Nothing Then title = titleNode.InnerText
            Dim resNode As XmlNode = document.SelectSingleNode("//didl:res[@duration]", namespaceManager)
            If resNode IsNot Nothing Then
                ' res@duration is the same "H:MM:SS[.f]" shape AVTransport uses elsewhere.
                Dim ms As Integer = ParseUpnpTime(resNode.Attributes("duration").Value)
                If ms > 0 Then durationMs = ms
            End If
        Catch ex As Exception
            LogError(ex, "ParseDidlMetadata")
        End Try
    End Sub

    ' Duration to report to the controller. MusicBee's own value wins when it has one (local file);
    ' for a remote URL it has none, so fall back to what the controller told us in the DIDL.
    Friend Shared Function RendererDurationMs() As Integer
        Dim durationMs As Integer = mbApiInterface.NowPlaying_GetDuration()
        If durationMs <= 0 Then durationMs = rendererCurrentDurationMs
        If durationMs < 0 Then durationMs = 0
        Return durationMs
    End Function

    ' Milliseconds -> UPnP "H:MM:SS" time string.
    Friend Shared Function FormatUpnpTime(milliseconds As Integer) As String
        If milliseconds < 0 Then milliseconds = 0
        Dim span As TimeSpan = TimeSpan.FromMilliseconds(milliseconds)
        Return String.Format("{0}:{1:00}:{2:00}", CInt(Math.Floor(span.TotalHours)), span.Minutes, span.Seconds)
    End Function

    ' UPnP "H:MM:SS[.f]" time string -> milliseconds. Returns -1 on parse failure.
    Friend Shared Function ParseUpnpTime(value As String) As Integer
        If String.IsNullOrEmpty(value) Then Return -1
        Try
            Dim head As String = value.Trim()
            If head.StartsWith("REL_TIME=", StringComparison.OrdinalIgnoreCase) Then head = head.Substring(9)
            Dim dot As Integer = head.IndexOf("."c)
            If dot >= 0 Then head = head.Substring(0, dot)
            Dim parts() As String = head.Split(":"c)
            If parts.Length <> 3 Then Return -1
            Dim h As Integer = Integer.Parse(parts(0))
            Dim m As Integer = Integer.Parse(parts(1))
            Dim s As Integer = Integer.Parse(parts(2))
            Return ((h * 3600) + (m * 60) + s) * 1000
        Catch
            Return -1
        End Try
    End Function

    ' Player_GetPlayState -> UPnP AVTransport CurrentTransportState.
    Friend Shared Function CurrentTransportState() As String
        Select Case mbApiInterface.Player_GetPlayState()
            Case PlayState.Playing
                Return "PLAYING"
            Case PlayState.Paused
                Return "PAUSED_PLAYBACK"
            Case PlayState.Loading
                Return "TRANSITIONING"
            Case PlayState.Stopped
                Return "STOPPED"
            Case Else
                Return "NO_MEDIA_PRESENT"
        End Select
    End Function

    ' ========================================================================
    ' AVTransport service
    ' ========================================================================
    <UpnpServiceVariable("TransportState", "string", True, "STOPPED", "PLAYING", "PAUSED_PLAYBACK", "TRANSITIONING", "NO_MEDIA_PRESENT")>
    <UpnpServiceVariable("TransportStatus", "string", True, "OK", "ERROR_OCCURRED")>
    <UpnpServiceVariable("CurrentTransportActions", "string", True)>
    <UpnpServiceVariable("CurrentPlayMode", "string", False, "NORMAL")>
    <UpnpServiceVariable("TransportPlaySpeed", "string", False, "1")>
    <UpnpServiceVariable("NumberOfTracks", "ui4", False)>
    <UpnpServiceVariable("CurrentTrack", "ui4", False)>
    <UpnpServiceVariable("CurrentTrackDuration", "string", False)>
    <UpnpServiceVariable("CurrentMediaDuration", "string", False)>
    <UpnpServiceVariable("CurrentTrackMetaData", "string", False)>
    <UpnpServiceVariable("CurrentTrackURI", "string", False)>
    <UpnpServiceVariable("AVTransportURI", "string", False)>
    <UpnpServiceVariable("AVTransportURIMetaData", "string", False)>
    <UpnpServiceVariable("NextAVTransportURI", "string", False)>
    <UpnpServiceVariable("NextAVTransportURIMetaData", "string", False)>
    <UpnpServiceVariable("RelativeTimePosition", "string", False)>
    <UpnpServiceVariable("AbsoluteTimePosition", "string", False)>
    <UpnpServiceVariable("RelativeCounterPosition", "i4", False)>
    <UpnpServiceVariable("AbsoluteCounterPosition", "i4", False)>
    <UpnpServiceVariable("PlaybackStorageMedium", "string", False, "NETWORK", "NONE")>
    <UpnpServiceVariable("RecordStorageMedium", "string", False, "NOT_IMPLEMENTED")>
    <UpnpServiceVariable("RecordMediumWriteStatus", "string", False, "NOT_IMPLEMENTED")>
    <UpnpServiceVariable("PossiblePlaybackStorageMedia", "string", False, "NETWORK")>
    <UpnpServiceVariable("PossibleRecordStorageMedia", "string", False, "NOT_IMPLEMENTED")>
    <UpnpServiceVariable("PossibleRecordQualityModes", "string", False, "NOT_IMPLEMENTED")>
    <UpnpServiceVariable("CurrentRecordQualityMode", "string", False, "NOT_IMPLEMENTED")>
    <UpnpServiceVariable("A_ARG_TYPE_InstanceID", "ui4", False)>
    <UpnpServiceVariable("A_ARG_TYPE_SeekMode", "string", False, "REL_TIME", "TRACK_NR")>
    <UpnpServiceVariable("A_ARG_TYPE_SeekTarget", "string", False)>
    Friend NotInheritable Class RendererAvTransportService
        Inherits UpnpService

        Public Sub New(server As UpnpServer)
            MyBase.New(server, "urn:schemas-upnp-org:service:AVTransport:1", "urn:upnp-org:serviceId:AVTransport", "/RendererAVTransport.control", "/RendererAVTransport.event", "/RendererAVTransport.xml")
        End Sub

        Protected Overrides Sub WriteEventProperty(writer As XmlWriter)
            ' AVTransport events carry a single LastChange property whose value is an escaped XML doc.
            Dim lastChange As New StringBuilder
            lastChange.Append("<Event xmlns=""urn:schemas-upnp-org:metadata-1-0/AVT/""><InstanceID val=""0"">")
            lastChange.Append("<TransportState val=""" & CurrentTransportState() & """/>")
            lastChange.Append("<TransportStatus val=""OK""/>")
            lastChange.Append("</InstanceID></Event>")
            writer.WriteStartElement("e", "property", Nothing)
            writer.WriteElementString("LastChange", lastChange.ToString())
            writer.WriteEndElement()
        End Sub

        <UpnpServiceArgument(0, "CurrentTransportActions", "CurrentTransportActions")>
        Private Sub GetCurrentTransportActions(request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            request.Response.SendSoapHeadersBody(request, "Play,Stop,Pause,Seek,Next,Previous")
        End Sub

        Private Sub SetAVTransportURI(request As HttpRequest,
                                      <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String,
                                      <UpnpServiceArgument("AVTransportURI")> CurrentURI As String,
                                      <UpnpServiceArgument("AVTransportURIMetaData")> CurrentURIMetaData As String)
            ' Resolve to a local library file first (loopback - bit-perfect); otherwise accept it as a
            ' remote http/https source for MusicBee to stream. Refuse anything we can't play at all.
            Dim uri As String = WebUtility.HtmlDecode(CurrentURI)
            Dim localPath As String = ResolveLoopbackFile(uri)
            Dim remoteUri As String = Nothing
            If localPath Is Nothing Then
                remoteUri = ResolveRemoteUri(uri)
                If remoteUri Is Nothing Then
                    LogInformation("Renderer:SetAVTransportURI", "refused unplayable uri=" & uri)
                    Throw New SoapException(716, "Resource not found")
                End If
            End If
            Dim title As String = Nothing
            Dim durationMs As Integer = 0
            ParseDidlMetadata(CurrentURIMetaData, title, durationMs)
            rendererCurrentUri = uri
            rendererCurrentLocalPath = localPath
            rendererCurrentRemoteUri = remoteUri
            rendererCurrentMetaData = WebUtility.HtmlDecode(If(CurrentURIMetaData, ""))
            rendererCurrentDurationMs = durationMs
            ' A newly-set URI must win over a resume: without this, Play() on a paused player would
            ' resume the PREVIOUS track and silently ignore the one the controller just loaded.
            rendererPendingUri = True
            LogInformation("Renderer:SetAVTransportURI", If(localPath IsNot Nothing, "loopback path=" & localPath, "remote uri=" & remoteUri) & ", title=" & If(title, "?") & ", duration=" & durationMs)
            request.Response.SendSoapHeadersBody(request)
        End Sub

        Private Sub Play(request As HttpRequest,
                         <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String,
                         <UpnpServiceArgument("TransportPlaySpeed")> Speed As String)
            Dim target As String = If(rendererCurrentLocalPath, rendererCurrentRemoteUri)
            If rendererPendingUri AndAlso target IsNot Nothing Then
                ' A URI was set since the last Play - start it, whatever the current transport state.
                rendererPendingUri = False
                ' Local path or remote URL, same call: MusicBee streams an http source the way it
                ' streams internet radio. False = MusicBee refused it outright (unknown format, host
                ' unreachable); say so instead of leaving the controller thinking playback started.
                If Not mbApiInterface.NowPlayingList_PlayNow(target) Then
                    LogInformation("Renderer:Play", "MusicBee refused target=" & target)
                    Throw New SoapException(716, "Resource not found")
                End If
            ElseIf mbApiInterface.Player_GetPlayState() = PlayState.Paused Then
                ' Resume.
                mbApiInterface.Player_PlayPause()
            ElseIf target IsNot Nothing Then
                ' Re-play the track already loaded (stopped, then Play again).
                mbApiInterface.NowPlayingList_PlayNow(target)
            Else
                mbApiInterface.Player_PlayPause()
            End If
            request.Response.SendSoapHeadersBody(request)
        End Sub

        Private Sub Pause(request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            If mbApiInterface.Player_GetPlayState() = PlayState.Playing Then
                mbApiInterface.Player_PlayPause()
            End If
            request.Response.SendSoapHeadersBody(request)
        End Sub

        Private Sub [Stop](request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            mbApiInterface.Player_Stop()
            request.Response.SendSoapHeadersBody(request)
        End Sub

        Private Sub [Next](request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            mbApiInterface.Player_PlayNextTrack()
            request.Response.SendSoapHeadersBody(request)
        End Sub

        Private Sub Previous(request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            mbApiInterface.Player_PlayPreviousTrack()
            request.Response.SendSoapHeadersBody(request)
        End Sub

        Private Sub Seek(request As HttpRequest,
                         <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String,
                         <UpnpServiceArgument("A_ARG_TYPE_SeekMode")> Unit As String,
                         <UpnpServiceArgument("A_ARG_TYPE_SeekTarget")> Target As String)
            If String.Equals(Unit, "REL_TIME", StringComparison.OrdinalIgnoreCase) Then
                Dim ms As Integer = ParseUpnpTime(Target)
                If ms >= 0 Then
                    mbApiInterface.Player_SetPosition(ms)
                End If
            End If
            request.Response.SendSoapHeadersBody(request)
        End Sub

        <UpnpServiceArgument(0, "CurrentTransportState", "TransportState")>
        <UpnpServiceArgument(1, "CurrentTransportStatus", "TransportStatus")>
        <UpnpServiceArgument(2, "CurrentSpeed", "TransportPlaySpeed")>
        Private Sub GetTransportInfo(request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            request.Response.SendSoapHeadersBody(request, CurrentTransportState(), "OK", "1")
        End Sub

        <UpnpServiceArgument(0, "Track", "CurrentTrack")>
        <UpnpServiceArgument(1, "TrackDuration", "CurrentTrackDuration")>
        <UpnpServiceArgument(2, "TrackMetaData", "CurrentTrackMetaData")>
        <UpnpServiceArgument(3, "TrackURI", "CurrentTrackURI")>
        <UpnpServiceArgument(4, "RelTime", "RelativeTimePosition")>
        <UpnpServiceArgument(5, "AbsTime", "AbsoluteTimePosition")>
        <UpnpServiceArgument(6, "RelCount", "RelativeCounterPosition")>
        <UpnpServiceArgument(7, "AbsCount", "AbsoluteCounterPosition")>
        Private Sub GetPositionInfo(request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            Dim positionMs As Integer = mbApiInterface.Player_GetPosition()
            ' Echo the controller's own DIDL back as TrackMetaData - for a remote source it is the
            ' only description of the track that exists on this side.
            request.Response.SendSoapHeadersBody(request, "1", FormatUpnpTime(RendererDurationMs()), rendererCurrentMetaData, If(rendererCurrentUri, ""), FormatUpnpTime(positionMs), FormatUpnpTime(positionMs), "0", "0")
        End Sub

        <UpnpServiceArgument(0, "NrTracks", "NumberOfTracks")>
        <UpnpServiceArgument(1, "MediaDuration", "CurrentMediaDuration")>
        <UpnpServiceArgument(2, "CurrentURI", "AVTransportURI")>
        <UpnpServiceArgument(3, "CurrentURIMetaData", "AVTransportURIMetaData")>
        <UpnpServiceArgument(4, "NextURI", "NextAVTransportURI")>
        <UpnpServiceArgument(5, "NextURIMetaData", "NextAVTransportURIMetaData")>
        <UpnpServiceArgument(6, "PlayMedium", "PlaybackStorageMedium")>
        <UpnpServiceArgument(7, "RecordMedium", "RecordStorageMedium")>
        <UpnpServiceArgument(8, "WriteStatus", "RecordMediumWriteStatus")>
        Private Sub GetMediaInfo(request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            request.Response.SendSoapHeadersBody(request, "1", FormatUpnpTime(RendererDurationMs()), If(rendererCurrentUri, ""), rendererCurrentMetaData, "", "", "NETWORK", "NOT_IMPLEMENTED", "NOT_IMPLEMENTED")
        End Sub

        <UpnpServiceArgument(0, "PlayMode", "CurrentPlayMode")>
        <UpnpServiceArgument(1, "RecQualityMode", "CurrentRecordQualityMode")>
        Private Sub GetTransportSettings(request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            request.Response.SendSoapHeadersBody(request, "NORMAL", "NOT_IMPLEMENTED")
        End Sub

        <UpnpServiceArgument(0, "PlayMedia", "PossiblePlaybackStorageMedia")>
        <UpnpServiceArgument(1, "RecMedia", "PossibleRecordStorageMedia")>
        <UpnpServiceArgument(2, "RecQualityModes", "PossibleRecordQualityModes")>
        Private Sub GetDeviceCapabilities(request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            request.Response.SendSoapHeadersBody(request, "NETWORK", "NOT_IMPLEMENTED", "NOT_IMPLEMENTED")
        End Sub
    End Class  ' RendererAvTransportService

    ' ========================================================================
    ' RenderingControl service
    ' ========================================================================
    <UpnpServiceVariable("Volume", "ui2", False)>
    <UpnpServiceVariable("Mute", "boolean", False)>
    <UpnpServiceVariable("PresetNameList", "string", False)>
    <UpnpServiceVariable("LastChange", "string", True)>
    <UpnpServiceVariable("A_ARG_TYPE_Channel", "string", False, "Master")>
    <UpnpServiceVariable("A_ARG_TYPE_InstanceID", "ui4", False)>
    <UpnpServiceVariable("A_ARG_TYPE_PresetName", "string", False, "FactoryDefaults")>
    Friend NotInheritable Class RendererRenderingControlService
        Inherits UpnpService

        Public Sub New(server As UpnpServer)
            MyBase.New(server, "urn:schemas-upnp-org:service:RenderingControl:1", "urn:upnp-org:serviceId:RenderingControl", "/RendererRenderingControl.control", "/RendererRenderingControl.event", "/RendererRenderingControl.xml")
        End Sub

        Protected Overrides Sub WriteEventProperty(writer As XmlWriter)
            Dim volume As Integer = CInt(Math.Round(mbApiInterface.Player_GetVolume() * 100))
            Dim lastChange As New StringBuilder
            lastChange.Append("<Event xmlns=""urn:schemas-upnp-org:metadata-1-0/RCS/""><InstanceID val=""0"">")
            lastChange.Append("<Volume channel=""Master"" val=""" & volume & """/>")
            lastChange.Append("<Mute channel=""Master"" val=""" & If(mbApiInterface.Player_GetMute(), "1", "0") & """/>")
            lastChange.Append("</InstanceID></Event>")
            writer.WriteStartElement("e", "property", Nothing)
            writer.WriteElementString("LastChange", lastChange.ToString())
            writer.WriteEndElement()
        End Sub

        <UpnpServiceArgument(0, "CurrentVolume", "Volume")>
        Private Sub GetVolume(request As HttpRequest,
                              <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String,
                              <UpnpServiceArgument("A_ARG_TYPE_Channel")> Channel As String)
            Dim volume As Integer = CInt(Math.Round(mbApiInterface.Player_GetVolume() * 100))
            request.Response.SendSoapHeadersBody(request, volume.ToString())
        End Sub

        Private Sub SetVolume(request As HttpRequest,
                              <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String,
                              <UpnpServiceArgument("A_ARG_TYPE_Channel")> Channel As String,
                              <UpnpServiceArgument("Volume")> DesiredVolume As String)
            Dim percent As Integer
            If Integer.TryParse(DesiredVolume, percent) Then
                If percent < 0 Then percent = 0
                If percent > 100 Then percent = 100
                mbApiInterface.Player_SetVolume(percent / 100.0F)
            End If
            request.Response.SendSoapHeadersBody(request)
        End Sub

        <UpnpServiceArgument(0, "CurrentMute", "Mute")>
        Private Sub GetMute(request As HttpRequest,
                            <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String,
                            <UpnpServiceArgument("A_ARG_TYPE_Channel")> Channel As String)
            request.Response.SendSoapHeadersBody(request, If(mbApiInterface.Player_GetMute(), "1", "0"))
        End Sub

        Private Sub SetMute(request As HttpRequest,
                            <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String,
                            <UpnpServiceArgument("A_ARG_TYPE_Channel")> Channel As String,
                            <UpnpServiceArgument("Mute")> DesiredMute As String)
            Dim mute As Boolean = (DesiredMute = "1" OrElse String.Equals(DesiredMute, "true", StringComparison.OrdinalIgnoreCase))
            mbApiInterface.Player_SetMute(mute)
            request.Response.SendSoapHeadersBody(request)
        End Sub

        <UpnpServiceArgument(0, "CurrentPresetNameList", "PresetNameList")>
        Private Sub ListPresets(request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String)
            request.Response.SendSoapHeadersBody(request, "FactoryDefaults")
        End Sub

        Private Sub SelectPreset(request As HttpRequest,
                                 <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String,
                                 <UpnpServiceArgument("A_ARG_TYPE_PresetName")> PresetName As String)
            request.Response.SendSoapHeadersBody(request)
        End Sub
    End Class  ' RendererRenderingControlService

    ' ========================================================================
    ' ConnectionManager service for the renderer (Sink protocol-info)
    ' ========================================================================
    <UpnpServiceVariable("SourceProtocolInfo", "string", True)>
    <UpnpServiceVariable("SinkProtocolInfo", "string", True)>
    <UpnpServiceVariable("CurrentConnectionIDs", "string", True)>
    <UpnpServiceVariable("A_ARG_TYPE_ConnectionStatus", "string", False, "OK", "ContentFormatMismatch", "InsufficientBandwidth", "UnreliableChannel", "Unknown")>
    <UpnpServiceVariable("A_ARG_TYPE_ConnectionManager", "string", False)>
    <UpnpServiceVariable("A_ARG_TYPE_Direction", "string", False, "Input", "Output")>
    <UpnpServiceVariable("A_ARG_TYPE_ProtocolInfo", "string", False)>
    <UpnpServiceVariable("A_ARG_TYPE_ConnectionID", "i4", False)>
    <UpnpServiceVariable("A_ARG_TYPE_AVTransportID", "i4", False)>
    <UpnpServiceVariable("A_ARG_TYPE_RcsID", "i4", False)>
    Friend NotInheritable Class RendererConnectionManagerService
        Inherits UpnpService

        ' What MusicBee (as a renderer) accepts. Mirrors the formats MusicBee's engine can play.
        Private Const sinkProtocolInfo As String = "http-get:*:audio/mpeg:*,http-get:*:audio/mp3:*,http-get:*:audio/x-mp3:*,http-get:*:audio/mp4:*,http-get:*:audio/m4a:*,http-get:*:audio/aac:*,http-get:*:audio/x-aac:*,http-get:*:audio/flac:*,http-get:*:audio/x-flac:*,http-get:*:audio/ogg:*,http-get:*:audio/x-ogg:*,http-get:*:audio/wav:*,http-get:*:audio/x-wav:*,http-get:*:audio/L16:*,http-get:*:audio/x-ms-wma:*,http-get:*:audio/wavpack:*,http-get:*:audio/x-wavpack:*,http-get:*:audio/musepack:*,http-get:*:audio/x-musepack:*"

        Public Sub New(server As UpnpServer)
            MyBase.New(server, "urn:schemas-upnp-org:service:ConnectionManager:1", "urn:upnp-org:serviceId:ConnectionManager", "/RendererConnectionManager.control", "/RendererConnectionManager.event", "/RendererConnectionManager.xml")
        End Sub

        Protected Overrides Sub WriteEventProperty(writer As XmlWriter)
            writer.WriteStartElement("e", "property", Nothing)
            writer.WriteElementString("SourceProtocolInfo", "")
            writer.WriteEndElement()
            writer.WriteStartElement("e", "property", Nothing)
            writer.WriteElementString("SinkProtocolInfo", sinkProtocolInfo)
            writer.WriteEndElement()
            writer.WriteStartElement("e", "property", Nothing)
            writer.WriteElementString("CurrentConnectionIDs", "0")
            writer.WriteEndElement()
        End Sub

        <UpnpServiceArgument(0, "Source", "SourceProtocolInfo")>
        <UpnpServiceArgument(1, "Sink", "SinkProtocolInfo")>
        Private Sub GetProtocolInfo(request As HttpRequest)
            request.Response.SendSoapHeadersBody(request, "", sinkProtocolInfo)
        End Sub

        <UpnpServiceArgument(0, "ConnectionIDs", "CurrentConnectionIDs")>
        Private Sub GetCurrentConnectionIDs(request As HttpRequest)
            request.Response.SendSoapHeadersBody(request, "0")
        End Sub

        <UpnpServiceArgument(0, "RcsID", "A_ARG_TYPE_RcsID")>
        <UpnpServiceArgument(1, "AVTransportID", "A_ARG_TYPE_AVTransportID")>
        <UpnpServiceArgument(2, "ProtocolInfo", "A_ARG_TYPE_ProtocolInfo")>
        <UpnpServiceArgument(3, "PeerConnectionManager", "A_ARG_TYPE_ConnectionManager")>
        <UpnpServiceArgument(4, "PeerConnectionID", "A_ARG_TYPE_ConnectionID")>
        <UpnpServiceArgument(5, "Direction", "A_ARG_TYPE_Direction")>
        <UpnpServiceArgument(6, "Status", "A_ARG_TYPE_ConnectionStatus")>
        Private Sub GetCurrentConnectionInfo(request As HttpRequest, <UpnpServiceArgument("A_ARG_TYPE_ConnectionID")> ConnectionID As String)
            request.Response.SendSoapHeadersBody(request, "0", "0", "", "", "-1", "Input", "OK")
        End Sub
    End Class  ' RendererConnectionManagerService

    ' Shared renderer state (single renderer instance / single InstanceID 0).
    Friend Shared rendererCurrentUri As String = ""
    ' Exactly one of these two is set once a URI is accepted: local wins (loopback), remote otherwise.
    Friend Shared rendererCurrentLocalPath As String = Nothing
    Friend Shared rendererCurrentRemoteUri As String = Nothing
    ' Controller-supplied DIDL, decoded. Echoed back as TrackMetaData; duration parsed out of it
    ' because MusicBee reports none for a URL outside its library.
    Friend Shared rendererCurrentMetaData As String = ""
    Friend Shared rendererCurrentDurationMs As Integer = 0
    ' True between SetAVTransportURI and the Play that consumes it - makes a fresh URI beat a resume.
    Friend Shared rendererPendingUri As Boolean = False

End Class
