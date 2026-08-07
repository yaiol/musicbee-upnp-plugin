Imports System.Text
Imports System.Xml
Imports System.Net
Imports System.Threading

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
    ' What the controller told us about the track it is handing over.
    Friend NotInheritable Class RendererDidl
        Public Title As String
        Public DurationMs As Integer
        Public ProtocolInfo As String
        Public LyricsUri As String
        Public UpnpClass As String
        Public SizeBytes As Long = -1

        ' Live stream (internet radio) rather than a file — the ONE question that decides whether a
        ' local copy is worth fetching. Downloading a broadcast is pointless twice over: it can never
        ' finish, and nobody seeks a live stream, so the copy would serve no purpose even if it could.
        '
        ' ⚠ This replaced a byte ceiling, and the difference matters. A size limit answers "is this
        ' big?", which is NOT the question: a 400MB DSD track and a radio stream both read as big, so
        ' the ceiling abandoned legitimate hi-res material while only delaying the runaway case. UPnP
        ' names the distinction outright, so ask it directly.
        Public ReadOnly Property IsBroadcast() As Boolean
            Get
                ' The standard class for internet radio: object.item.audioItem.audioBroadcast.
                If UpnpClass IsNot Nothing AndAlso UpnpClass.IndexOf("audioBroadcast", StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
                ' A sender that omits the class still gives itself away: a finite file states a size
                ' or a duration (both senders we have seen state both), a live one can state neither.
                Return SizeBytes <= 0 AndAlso DurationMs <= 0
            End Get
        End Property
    End Class

    Friend Shared Function ParseDidlMetadata(metadata As String) As RendererDidl
        Dim didl As New RendererDidl()
        If String.IsNullOrEmpty(metadata) Then Return didl
        Try
            Dim xml As String = WebUtility.HtmlDecode(metadata).Trim()
            If Not xml.StartsWith("<") Then Return didl
            Dim document As New XmlDocument()
            document.LoadXml(xml)
            Dim namespaceManager As New XmlNamespaceManager(document.NameTable)
            namespaceManager.AddNamespace("didl", "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/")
            namespaceManager.AddNamespace("dc", "http://purl.org/dc/elements/1.1/")
            namespaceManager.AddNamespace("upnp", "urn:schemas-upnp-org:metadata-1-0/upnp/")
            Dim titleNode As XmlNode = document.SelectSingleNode("//dc:title", namespaceManager)
            If titleNode IsNot Nothing Then didl.Title = titleNode.InnerText
            ' upnp:class - object.item.audioItem.musicTrack for a file, ...audioBroadcast for radio.
            Dim classNode As XmlNode = document.SelectSingleNode("//upnp:class", namespaceManager)
            If classNode IsNot Nothing Then didl.UpnpClass = classNode.InnerText
            Dim resNode As XmlNode = document.SelectSingleNode("//didl:res[@duration]", namespaceManager)
            If resNode IsNot Nothing Then
                ' res@duration is the same "H:MM:SS[.f]" shape AVTransport uses elsewhere.
                Dim ms As Integer = ParseUpnpTime(resNode.Attributes("duration").Value)
                If ms > 0 Then didl.DurationMs = ms
            End If
            ' protocolInfo ("http-get:*:audio/mpeg:*") names the MIME type - the only reliable way to
            ' pick a file extension for the local copy when the URL has none.
            Dim protocolNode As XmlNode = document.SelectSingleNode("//didl:res[@protocolInfo]", namespaceManager)
            If protocolNode IsNot Nothing Then didl.ProtocolInfo = protocolNode.Attributes("protocolInfo").Value
            ' res@size - the byte length, stated BEFORE we connect. Says "this is a finite file" and
            ' by how much, so the first play can judge the wait without waiting on HTTP headers.
            Dim sizeNode As XmlNode = document.SelectSingleNode("//didl:res[@size]", namespaceManager)
            If sizeNode IsNot Nothing Then
                Dim bytes As Long
                If Long.TryParse(sizeNode.Attributes("size").Value, bytes) AndAlso bytes > 0 Then didl.SizeBytes = bytes
            End If
            ' upnp:lyricsURI - the ONE standard place a controller can point at an external .lrc.
            ' Optional in the spec and widely omitted; if a sender does provide it we could fetch it
            ' alongside the audio and drop it beside the temp copy as a sidecar. Read here so the
            ' log can answer "does this sender send one?" from a real cast rather than a guess.
            Dim lyricsNode As XmlNode = document.SelectSingleNode("//upnp:lyricsURI", namespaceManager)
            If lyricsNode IsNot Nothing Then didl.LyricsUri = lyricsNode.InnerText
        Catch ex As Exception
            LogError(ex, "ParseDidlMetadata")
        End Try
        Return didl
    End Function

    ' Duration to report to the controller. MusicBee's own value wins when it has one (local file);
    ' for a remote URL it has none, so fall back to what the controller told us in the DIDL.
    Friend Shared Function RendererDurationMs() As Integer
        Dim durationMs As Integer = mbApiInterface.NowPlaying_GetDuration()
        If durationMs <= 0 Then durationMs = rendererCurrentDurationMs
        If durationMs < 0 Then durationMs = 0
        Return durationMs
    End Function

    ' ========================================================================
    ' Remote-source cache - "fetch to temp, swap on seek"
    ' ========================================================================
    ' MusicBee treats ANY http:// source as a stream and will not reposition it: Player_SetPosition
    ' returns True, Player_GetPosition reads back the target, and then the decoder - which never
    ' moved - overwrites it a fraction of a second later, so the controller's slider snaps back.
    ' This is NOT the sender's fault: BubbleUPnP's media server answers Range requests correctly
    ' (206 + Content-Range + Accept-Ranges), which is why proxying the stream would change nothing.
    '
    ' So a remote cast is fetched twice over. MusicBee streams the URL directly - instant start,
    ' that is what the listener hears - while we download the same file to %TEMP% in the background.
    ' The moment a Seek arrives we swap playback onto the downloaded copy, which is an ordinary local
    ' file and seeks normally; every later seek on that track is then instant.
    '
    ' The good property of doing both: an endless stream (internet radio) simply never finishes
    ' downloading, so seeking stays unavailable and nothing hangs waiting to buffer. The size cap
    ' below is what stops such a stream filling the disk - it is a safety valve, not a tuning knob.
    ' Last-resort backstop against a sender that lies or states nothing — NOT a judgement about track
    ' size. The live-stream question is answered properly by the DIDL (RendererDidl.IsBroadcast), so
    ' this only has to sit above anything real: a 20-minute 24/96 FLAC is ~375MB and a 10-minute DSD64
    ' ~420MB, which the old 300MB ceiling abandoned outright. Never lower it to "save space" — space
    ' was never the problem it solved.
    Private Const rendererCacheMaxBytes As Long = 2L * 1024L * 1024L * 1024L
    ' How long a Seek waits for an in-flight download before giving up and reporting 710. Covers the
    ' case of seeking within a second or two of starting a track.
    Private Const rendererCacheGraceMs As Integer = 2000
    ' How long the FIRST play waits for the local copy. Long enough for a normal track over a LAN
    ' (measured around half a second), short enough that a slow sender falls back to streaming rather
    ' than leaving the user staring at silence. Exceeding it costs the title and instant seeking,
    ' never the playback itself.
    Private Const rendererCacheFirstPlayWaitMs As Integer = 3000
    ' Never hardcode the product name. UpdateCheck.ProductName() reads AssemblyProduct, which app-info
    ' writes from info.json - so a rename propagates here like it does everywhere else.
    Private Shared ReadOnly rendererCacheLock As New Object
    ' ⚠ SEVERAL entries, not one. A controller does not wait politely: Symfonium announces the NEXT
    ' track ~150ms after the current one, both as SetAVTransportURI. With a single slot the second
    ' announcement repurposed it, so the Play for the FIRST track found a slot belonging to another
    ' URI, gave up instantly and streamed the remote URL - losing exactly the title and the seeking
    ' this cache exists to provide. On an album that is nearly every track. Keyed by URI and holding
    ' the last few, an announcement can no longer destroy the copy another command is about to need.
    ' (Two entries is the requirement - see the eviction comment in StartRendererCacheDownload for
    ' why that number comes from the protocol, and what the third is actually for.)
    ' (Evicting is always safe: MusicBee reads a track into memory, so deleting a temp file it is
    ' playing does not interrupt it.)
    Private Const rendererCacheMaxEntries As Integer = 3
    Private Shared ReadOnly rendererCacheEntries As New List(Of RendererCacheEntry)
    ' The local copy MusicBee is currently playing after a swap. Nothing while playing the remote
    ' URL, so a later Seek knows it still has to swap.
    Private Shared rendererPlayingCacheFile As String = Nothing
    ' Above this we don't hold up playback waiting for the copy - the wait would just run its timeout
    ' down and fall back to streaming anyway. Seeking still works once the download lands.
    Private Const rendererCacheWaitMaxBytes As Long = 64L * 1024L * 1024L

    Friend NotInheritable Class RendererCacheEntry
        Public ReadOnly Uri As String
        Public ReadOnly Path As String
        ' True from creation until the worker finishes, whether it succeeded or not.
        Public Downloading As Boolean = True
        Public Ready As Boolean = False
        ' What the sender advertised: 0 = headers not seen yet, -1 = none given (an endless stream),
        ' >0 = the real length. Lets the first play tell "a second away" from "never going to finish".
        Public ExpectedBytes As Long = 0
        Public Sub New(uri As String, path As String)
            Me.Uri = uri
            Me.Path = path
        End Sub
    End Class

    ' ⚠ Call under rendererCacheLock.
    Private Shared Function FindRendererCacheEntry(uri As String) As RendererCacheEntry
        If uri Is Nothing Then Return Nothing
        For Each entry As RendererCacheEntry In rendererCacheEntries
            If String.Equals(entry.Uri, uri, StringComparison.Ordinal) Then Return entry
        Next entry
        Return Nothing
    End Function

    ' %TEMP%\<product name>\ - spaces stripped so the path stays easy to type and to read in a log.
    Private Shared Function RendererCacheFolderPath() As String
        Dim name As New StringBuilder
        For Each character As Char In UpdateCheck.ProductName()
            If Not Char.IsWhiteSpace(character) AndAlso Array.IndexOf(IO.Path.GetInvalidFileNameChars(), character) < 0 Then
                name.Append(character)
            End If
        Next character
        Return IO.Path.Combine(IO.Path.GetTempPath(), name.ToString())
    End Function

    Private Shared Function RendererCacheFolder() As String
        Dim folder As String = RendererCacheFolderPath()
        If Not IO.Directory.Exists(folder) Then IO.Directory.CreateDirectory(folder)
        Return folder
    End Function

    ' Called once at startup: MusicBee may have been killed mid-cast, leaving copies behind.
    Friend Shared Sub ClearRendererCacheFolder()
        Try
            Dim folder As String = RendererCacheFolderPath()
            If Not IO.Directory.Exists(folder) Then Exit Sub
            For Each file As String In IO.Directory.GetFiles(folder)
                Try
                    IO.File.Delete(file)
                Catch
                    ' Still locked by something - it will be swept on a later start.
                End Try
            Next file
        Catch ex As Exception
            LogError(ex, "Renderer:ClearRendererCacheFolder")
        End Try
    End Sub

    Private Shared Sub DeleteRendererCacheFile(path As String)
        If String.IsNullOrEmpty(path) Then Exit Sub
        Try
            If IO.File.Exists(path) Then IO.File.Delete(path)
        Catch ex As Exception
            ' MusicBee may still hold it open (we swapped playback onto it). Not worth retrying -
            ' ClearRendererCacheFolder sweeps it on the next start.
            LogInformation("Renderer:Cache", "could not delete " & path & " - " & ex.Message)
        End Try
    End Sub

    ' The extension the local copy must carry. MusicBee picks its decoder from the extension, so a
    ' wrong guess is worse than none - when neither the URL nor the DIDL names a known audio type we
    ' return Nothing and simply don't cache (seeking stays unavailable, playback is unaffected).
    Private Shared Function RendererCacheExtension(uri As String, protocolInfo As String) As String
        Dim fromUrl As String = UrlAudioExtension(uri)
        If fromUrl IsNot Nothing Then Return fromUrl
        Return MimeAudioExtension(protocolInfo)
    End Function

    ' The URL's own extension, when it names an audio type we recognise. Also decides whether the
    ' first play has to wait for the local copy: a URL MusicBee can't type is one whose tags it will
    ' never read, so the track would otherwise show as a bare URL with no title.
    Private Shared Function UrlAudioExtension(uri As String) As String
        Dim known() As String = {".mp3", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".wv", ".mpc", ".ape", ".aiff", ".aif", ".dsf", ".dff"}
        Try
            Dim parsed As New Uri(uri)
            Dim extension As String = IO.Path.GetExtension(parsed.AbsolutePath)
            If Not String.IsNullOrEmpty(extension) Then
                extension = extension.ToLowerInvariant()
                If Array.IndexOf(known, extension) >= 0 Then Return extension
            End If
        Catch
            ' Not a parsable URL - treated as "no usable extension".
        End Try
        Return Nothing
    End Function

    Private Shared Function MimeAudioExtension(protocolInfo As String) As String
        If String.IsNullOrEmpty(protocolInfo) Then Return Nothing
        ' "http-get:*:audio/mpeg:DLNA.ORG_PN=MP3" -> the third colon-separated field is the MIME type.
        Dim fields() As String = protocolInfo.Split(":"c)
        If fields.Length < 3 Then Return Nothing
        Select Case fields(2).Trim().ToLowerInvariant()
            Case "audio/mpeg", "audio/mp3", "audio/x-mp3", "audio/mpeg3", "audio/x-mpeg-3"
                Return ".mp3"
            Case "audio/flac", "audio/x-flac"
                Return ".flac"
            Case "audio/mp4", "audio/m4a", "audio/x-m4a", "audio/aac", "audio/x-aac"
                Return ".m4a"
            Case "audio/ogg", "audio/x-ogg", "application/ogg", "audio/vorbis"
                Return ".ogg"
            Case "audio/opus"
                Return ".opus"
            Case "audio/wav", "audio/x-wav", "audio/wave", "audio/vnd.wave"
                Return ".wav"
            Case "audio/x-ms-wma"
                Return ".wma"
            Case "audio/x-wavpack", "audio/wavpack"
                Return ".wv"
            Case "audio/x-musepack", "audio/musepack"
                Return ".mpc"
            Case "audio/x-monkeys-audio", "audio/ape"
                Return ".ape"
            Case "audio/aiff", "audio/x-aiff"
                Return ".aiff"
        End Select
        Return Nothing
    End Function

    Private Shared Sub StartRendererCacheDownload(uri As String, didl As RendererDidl)
        ' A live stream is the one thing never worth fetching: it cannot finish, and nobody seeks
        ' radio. Asking the DIDL what the thing IS beats guessing from how big it has grown.
        If didl.IsBroadcast Then
            LogInformation("Renderer:Cache", "live stream (class=" & If(didl.UpnpClass, "none") & ", size=" & didl.SizeBytes & ", duration=" & didl.DurationMs & ") - not caching, seek stays unavailable")
            Exit Sub
        End If
        Dim extension As String = RendererCacheExtension(uri, didl.ProtocolInfo)
        If extension Is Nothing Then
            LogInformation("Renderer:Cache", "no known audio extension for uri=" & uri & ", protocolInfo=" & If(didl.ProtocolInfo, "?") & " - not caching, seek stays unavailable")
            Exit Sub
        End If
        Dim target As String
        Try
            target = IO.Path.Combine(RendererCacheFolder(), "cast-" & Guid.NewGuid().ToString("N") & extension)
        Catch ex As Exception
            LogError(ex, "Renderer:StartRendererCacheDownload", "uri=" & uri)
            Exit Sub
        End Try
        Dim evicted As New List(Of String)
        SyncLock rendererCacheLock
            ' Already held or already coming - a controller re-announcing the same track (Symfonium
            ' does, on every queue nudge) must not start a second download of it.
            If FindRendererCacheEntry(uri) IsNot Nothing Then Exit Sub
            Dim entry As New RendererCacheEntry(uri, target)
            ' Seed the size from the DIDL: the sender states it up front, so the first play can judge
            ' whether waiting is worth it immediately instead of waiting on the HTTP headers first.
            If didl.SizeBytes > 0 Then entry.ExpectedBytes = didl.SizeBytes
            rendererCacheEntries.Add(entry)
            ' Oldest out first. TWO is the real requirement, and it is a protocol fact rather than an
            ' observation: AVTransport gives a renderer exactly one CURRENT and one NEXT URI slot,
            ' with no queue behind them, so a controller has nowhere to put a third and no reason to
            ' send a track it is not about to play. The third slot here buys one concrete thing -
            ' skipping BACKWARDS: Previous makes the controller re-announce the previous track's URI,
            ' which is then still on disk instead of being fetched again. That, at ~13MB, is the whole
            ' justification; it is not headroom against some unseen controller.
            Do While rendererCacheEntries.Count > rendererCacheMaxEntries
                evicted.Add(rendererCacheEntries(0).Path)
                rendererCacheEntries.RemoveAt(0)
            Loop
        End SyncLock
        For Each path As String In evicted
            DeleteRendererCacheFile(path)
        Next path
        ThreadPool.QueueUserWorkItem(AddressOf RendererCacheWorker, New String() {uri, target})
    End Sub

    Private Shared Sub RendererCacheWorker(state As Object)
        Dim parameters() As String = DirectCast(state, String())
        Dim uri As String = parameters(0)
        Dim target As String = parameters(1)
        Dim complete As Boolean = False
        Try
            Dim request As HttpWebRequest = DirectCast(WebRequest.Create(uri), HttpWebRequest)
            request.Timeout = 15000
            request.ReadWriteTimeout = 30000
            request.UserAgent = "MusicBee UPnP Plugin"
            Using response As HttpWebResponse = DirectCast(request.GetResponse(), HttpWebResponse)
                SyncLock rendererCacheLock
                    ' Publish the advertised size the moment we know it, so a first play that is
                    ' waiting can decide whether waiting is worth it at all.
                    Dim opening As RendererCacheEntry = FindRendererCacheEntry(uri)
                    If opening IsNot Nothing Then
                        opening.ExpectedBytes = If(response.ContentLength > 0, response.ContentLength, -1L)
                    End If
                End SyncLock
                Using source As IO.Stream = response.GetResponseStream()
                    Using destination As New IO.FileStream(target, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.Read, 65536)
                        Dim buffer(65535) As Byte
                        Dim total As Long = 0
                        Do
                            Dim count As Integer = source.Read(buffer, 0, buffer.Length)
                            If count <= 0 Then
                                complete = (total > 0)
                                Exit Do
                            End If
                            total += count
                            If total > rendererCacheMaxBytes Then
                                ' Endless stream, or a file far larger than any track. Give up on
                                ' caching it rather than filling the disk; playback is unaffected.
                                LogInformation("Renderer:Cache", "abandoned at " & total & " bytes (cap) uri=" & uri)
                                Exit Do
                            End If
                            destination.Write(buffer, 0, count)
                        Loop
                    End Using
                End Using
            End Using
        Catch ex As Exception
            LogError(ex, "Renderer:RendererCacheWorker", "uri=" & uri)
        End Try
        SyncLock rendererCacheLock
            ' The entry may have been evicted while this ran - then the copy is already stale.
            Dim entry As RendererCacheEntry = FindRendererCacheEntry(uri)
            If entry Is Nothing OrElse Not String.Equals(entry.Path, target, StringComparison.OrdinalIgnoreCase) Then
                complete = False
            Else
                entry.Downloading = False
                entry.Ready = complete
                If Not complete Then rendererCacheEntries.Remove(entry)
            End If
        End SyncLock
        If complete Then
            LogInformation("Renderer:Cache", "ready " & target)
        Else
            DeleteRendererCacheFile(target)
        End If
    End Sub

    ' The finished local copy of uri, waiting up to timeoutMs for a download still in flight.
    ' Nothing = no copy will be coming (never started, failed, capped, or superseded).
    ' beforeFirstPlay = the caller is holding playback back, so give up early on anything that can't
    ' land in time rather than burning the whole timeout in silence. A Seek passes False: by then the
    ' listener is already hearing the track and waiting the full grace period costs nothing.
    Private Shared Function WaitForRendererCache(uri As String, timeoutMs As Integer, beforeFirstPlay As Boolean) As String
        Dim deadline As Long = DateTime.UtcNow.Ticks + (CLng(timeoutMs) * TimeSpan.TicksPerMillisecond)
        Do
            SyncLock rendererCacheLock
                Dim entry As RendererCacheEntry = FindRendererCacheEntry(uri)
                If entry Is Nothing Then Return Nothing
                If entry.Ready Then Return entry.Path
                If Not entry.Downloading Then Return Nothing
                If beforeFirstPlay AndAlso entry.ExpectedBytes <> 0 Then
                    ' Headers are in. No length = an endless stream, which will never finish; too big
                    ' = the timeout would expire before it does. Start streaming instead, now.
                    If entry.ExpectedBytes < 0 OrElse entry.ExpectedBytes > rendererCacheWaitMaxBytes Then
                        LogInformation("Renderer:Cache", "not waiting for local copy, advertised size=" & entry.ExpectedBytes)
                        Return Nothing
                    End If
                End If
            End SyncLock
            Thread.Sleep(100)
        Loop While DateTime.UtcNow.Ticks < deadline
        Return Nothing
    End Function

    ' NowPlayingList_PlayNow returns before MusicBee has opened the file, and setting a position on a
    ' track it hasn't opened is a no-op. Wait for it to leave the loading state.
    Private Shared Sub WaitForRendererPlayback(timeoutMs As Integer)
        Dim deadline As Long = DateTime.UtcNow.Ticks + (CLng(timeoutMs) * TimeSpan.TicksPerMillisecond)
        Do
            Dim state As PlayState = mbApiInterface.Player_GetPlayState()
            If state = PlayState.Playing OrElse state = PlayState.Paused Then Exit Do
            Thread.Sleep(50)
        Loop While DateTime.UtcNow.Ticks < deadline
    End Sub

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
            Dim didl As RendererDidl = ParseDidlMetadata(CurrentURIMetaData)
            Dim title As String = didl.Title
            Dim durationMs As Integer = didl.DurationMs
            rendererCurrentUri = uri
            rendererCurrentLocalPath = localPath
            rendererCurrentRemoteUri = remoteUri
            rendererCurrentMetaData = WebUtility.HtmlDecode(If(CurrentURIMetaData, ""))
            rendererCurrentDurationMs = durationMs
            ' A newly-set URI must win over a resume: without this, Play() on a paused player would
            ' resume the PREVIOUS track and silently ignore the one the controller just loaded.
            rendererPendingUri = True
            ' A new CURRENT track invalidates whatever was announced as following the old one.
            ClearRendererNext()
            ' Drop the previous cast's local copy, then start fetching this one in the background so
            ' a later Seek has a seekable file to swap onto. Playback itself does not wait for it.
            If remoteUri IsNot Nothing Then
                StartRendererCacheDownload(remoteUri, didl)
            End If
            LogInformation("Renderer:SetAVTransportURI", If(localPath IsNot Nothing, "loopback path=" & localPath, "remote uri=" & remoteUri) & ", title=" & If(title, "?") & ", duration=" & durationMs)
            ' DIAGNOSTIC (external lyrics): does this controller advertise a .lrc at all? Reports the
            ' standard upnp:lyricsURI, then dumps the raw DIDL so a NON-standard element can be spotted
            ' too - several servers invent their own rather than use the spec's. Capped: a DIDL is
            ' normally under 2 KB, but nothing guarantees it and this must not flood the log.
            LogInformation("Renderer:DIDL", "class=" & If(didl.UpnpClass, "(none)") & ", size=" & didl.SizeBytes & ", broadcast=" & didl.IsBroadcast & ", lyricsURI=" & If(didl.LyricsUri, "(none)") & ", raw=" &
                If(rendererCurrentMetaData.Length > 4000, rendererCurrentMetaData.Substring(0, 4000) & "…[truncated]", rendererCurrentMetaData))
            request.Response.SendSoapHeadersBody(request)
        End Sub

        ' The second URI slot - "here is what follows the current track". Implementing it is what lets
        ' a controller pre-load properly instead of faking it with a second SetAVTransportURI, and it
        ' is what makes an album cast from a phone play without a gap at every track boundary.
        Private Sub SetNextAVTransportURI(request As HttpRequest,
                                          <UpnpServiceArgument("A_ARG_TYPE_InstanceID")> InstanceID As String,
                                          <UpnpServiceArgument("NextAVTransportURI")> NextURI As String,
                                          <UpnpServiceArgument("NextAVTransportURIMetaData")> NextURIMetaData As String)
            Dim uri As String = WebUtility.HtmlDecode(If(NextURI, "")).Trim()
            If uri.Length = 0 Then
                ' An empty NextURI is the defined way to say "forget it" - not an error.
                ClearRendererNext()
                LogInformation("Renderer:SetNextAVTransportURI", "cleared")
                request.Response.SendSoapHeadersBody(request)
                Exit Sub
            End If
            Dim localPath As String = ResolveLoopbackFile(uri)
            Dim remoteUri As String = Nothing
            If localPath Is Nothing Then
                remoteUri = ResolveRemoteUri(uri)
                If remoteUri Is Nothing Then
                    LogInformation("Renderer:SetNextAVTransportURI", "refused unplayable uri=" & uri)
                    Throw New SoapException(716, "Resource not found")
                End If
            End If
            Dim didl As RendererDidl = ParseDidlMetadata(NextURIMetaData)
            rendererNextUri = uri
            rendererNextLocalPath = localPath
            rendererNextRemoteUri = remoteUri
            rendererNextMetaData = WebUtility.HtmlDecode(If(NextURIMetaData, ""))
            rendererNextDurationMs = didl.DurationMs
            rendererQueuedNextFile = Nothing
            ' Start fetching it now - there is a whole track's playing time to get it down, so the
            ' next track can begin from a local copy rather than a stream, like the current one.
            If remoteUri IsNot Nothing Then StartRendererCacheDownload(remoteUri, didl)
            ThreadPool.QueueUserWorkItem(AddressOf RendererQueueNextWorker, uri)
            LogInformation("Renderer:SetNextAVTransportURI", If(localPath IsNot Nothing, "loopback path=" & localPath, "remote uri=" & remoteUri) & ", title=" & If(didl.Title, "?") & ", duration=" & didl.DurationMs)
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
                ' EVERY remote source starts from the local copy when one can be had in time. Not just
                ' the ones whose URL looks wrong: we only ever see two senders, and how a third names
                ' its URLs is unknowable - so don't branch on a guess about the sender. Starting local
                ' also means the track is seekable from the first note, with no swap and no first-
                ' seconds refusal. Falls back to streaming when the copy can't arrive in time.
                Dim playingCache As String = Nothing
                If rendererCurrentLocalPath Is Nothing AndAlso rendererCurrentRemoteUri IsNot Nothing Then
                    playingCache = WaitForRendererCache(rendererCurrentRemoteUri, rendererCacheFirstPlayWaitMs, True)
                    If playingCache Is Nothing Then
                        ' Deliberately not "waited Nms": the wait also returns at once when there is
                        ' no entry, or when the headers said it can't land in time. Claiming a
                        ' duration that didn't happen sends the next investigation the wrong way.
                        LogInformation("Renderer:Play", "no local copy available - streaming the remote url; title may show as the url and seeking needs a swap")
                    End If
                End If
                Dim startFrom As String = If(playingCache, target)
                If Not mbApiInterface.NowPlayingList_PlayNow(startFrom) Then
                    LogInformation("Renderer:Play", "MusicBee refused target=" & startFrom)
                    Throw New SoapException(716, "Resource not found")
                End If
                ' Nothing when we started from the remote URL - so a later Seek knows it must swap.
                rendererPlayingCacheFile = playingCache
                LogInformation("Renderer:Play", "new uri, started " & If(playingCache IsNot Nothing, "local copy " & playingCache, startFrom))
            ElseIf mbApiInterface.Player_GetPlayState() = PlayState.Paused Then
                ' Resume.
                mbApiInterface.Player_PlayPause()
                LogInformation("Renderer:Play", "resumed")
            ElseIf target IsNot Nothing Then
                ' Re-play the track already loaded (stopped, then Play again). ⚠ DIAGNOSTIC: this is
                ' also the branch a bare Play lands in when a DIFFERENT controller connects and presses
                ' play before setting its own URI - "target" is then the previous controller's track.
                mbApiInterface.NowPlayingList_PlayNow(target)
                rendererPlayingCacheFile = Nothing
                LogInformation("Renderer:Play", "no new uri - replayed previous target=" & target)
            Else
                mbApiInterface.Player_PlayPause()
                LogInformation("Renderer:Play", "nothing loaded - bare play/pause toggle")
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
            ' DIAGNOSTIC (BubbleUPnP slider snaps back after ~1s): the controller polls
            ' GetPositionInfo and gets the OLD position, so the seek isn't reaching MusicBee. This
            ' logs every step of the chain so one test says WHICH step fails — the seek mode the
            ' controller actually sends, whether the target parsed, what Player_SetPosition returned,
            ' and whether the position actually moved. Guessing twice already cost us enough.
            Dim positionBefore As Integer = mbApiInterface.Player_GetPosition()
            If Not String.Equals(Unit, "REL_TIME", StringComparison.OrdinalIgnoreCase) Then
                ' Our SCPD advertises REL_TIME + TRACK_NR; anything else we genuinely cannot honour.
                ' Say so instead of answering success and leaving the controller to believe it worked.
                LogInformation("Renderer:Seek", "unsupported mode=" & If(Unit, "?") & ", target=" & If(Target, "?"))
                Throw New SoapException(710, "Seek mode not supported")
            End If
            Dim ms As Integer = ParseUpnpTime(Target)
            If ms < 0 Then
                LogInformation("Renderer:Seek", "unparsable target=" & If(Target, "?"))
                Throw New SoapException(711, "Illegal seek target")
            End If
            ' A remote source is a stream to MusicBee and cannot be repositioned, so swap playback onto
            ' the background-downloaded copy first - an ordinary local file, which seeks normally.
            Dim swapped As Boolean = False
            If rendererCurrentLocalPath Is Nothing AndAlso rendererCurrentRemoteUri IsNot Nothing Then
                Dim cached As String = WaitForRendererCache(rendererCurrentRemoteUri, rendererCacheGraceMs, False)
                If cached Is Nothing Then
                    ' Still downloading (a seek in the first seconds), or never cacheable at all.
                    ' Refuse honestly - the controller reports it and a second attempt usually works -
                    ' rather than accepting and letting the slider snap back with no explanation.
                    LogInformation("Renderer:Seek", "no local copy available for " & rendererCurrentRemoteUri & " - refusing")
                    Throw New SoapException(710, "Seek mode not supported")
                End If
                If Not String.Equals(rendererPlayingCacheFile, cached, StringComparison.OrdinalIgnoreCase) Then
                    If Not mbApiInterface.NowPlayingList_PlayNow(cached) Then
                        LogInformation("Renderer:Seek", "MusicBee refused local copy " & cached)
                        Throw New SoapException(716, "Resource not found")
                    End If
                    rendererPlayingCacheFile = cached
                    swapped = True
                    WaitForRendererPlayback(1500)
                End If
            End If
            ' Retry: right after a swap MusicBee can still be opening the file, and a position set
            ' then is silently dropped.
            Dim accepted As Boolean = False
            Dim positionAfter As Integer = positionBefore
            For attempt As Integer = 1 To 3
                accepted = mbApiInterface.Player_SetPosition(ms)
                positionAfter = mbApiInterface.Player_GetPosition()
                If Math.Abs(positionAfter - ms) <= 3000 Then Exit For
                Thread.Sleep(200)
            Next attempt
            LogInformation("Renderer:Seek", "target=" & Target & " (" & ms & "ms), accepted=" & accepted & ", position " & positionBefore & "->" & positionAfter & ", source=" & If(rendererCurrentLocalPath IsNot Nothing, "loopback", If(rendererPlayingCacheFile IsNot Nothing, "remote/local-copy", "remote")) & If(swapped, " (swapped)", ""))
            ' Log the next few position polls too — the snap-back happens in those, not here.
            rendererPositionLogBudget = 5
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
            ' DIAGNOSTIC, rate-limited: control points poll this about once a second, so logging
            ' every call would bury the log. Log only the handful of polls right after a Seek (where
            ' the snap-back shows up) plus an occasional heartbeat to prove polling is happening.
            If rendererPositionLogBudget > 0 Then
                rendererPositionLogBudget -= 1
                LogInformation("Renderer:GetPositionInfo", "post-seek poll, position=" & positionMs & ", duration=" & RendererDurationMs())
            Else
                rendererPositionPollCount += 1
                If rendererPositionPollCount >= 60 Then
                    rendererPositionPollCount = 0
                    LogInformation("Renderer:GetPositionInfo", "heartbeat, position=" & positionMs & ", duration=" & RendererDurationMs())
                End If
            End If
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
            request.Response.SendSoapHeadersBody(request, "1", FormatUpnpTime(RendererDurationMs()), If(rendererCurrentUri, ""), rendererCurrentMetaData, If(rendererNextUri, ""), rendererNextMetaData, "NETWORK", "NOT_IMPLEMENTED", "NOT_IMPLEMENTED")
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
    ' The range is MANDATORY, not decoration: it is how a controller learns what "maximum volume"
    ' means here. Without it Symfonium guessed 69, so its 100% only reached 69% in MusicBee and
    ' MusicBee's 100% read back as 144% on the phone - one wrong maximum, wrong in both directions.
    <UpnpServiceVariable("Volume", "ui2", False, Minimum:="0", Maximum:="100", [Step]:="1")>
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

    ' ── The NEXT track (SetNextAVTransportURI) ───────────────────────────────────────────────────
    ' AVTransport gives a renderer a second slot so a controller can say "here is what follows" and
    ' the renderer can roll into it with no gap. It is OPTIONAL, and we did not implement it - so a
    ' controller wanting to pre-load had nowhere to put the track and did the only thing left: a
    ' second SetAVTransportURI, 150ms after the first (Symfonium, observed 2026-08-03). That is what
    ' a missing next slot looks like from the outside, and it cost us a whole bug.
    '
    ' The transition itself is MusicBee's job, not ours: we hand it the track with
    ' NowPlayingList_QueueNext and its own player crosses the boundary gaplessly. We only promote the
    ' bookkeeping afterwards, on TrackChanged.
    Friend Shared rendererNextUri As String = Nothing
    Friend Shared rendererNextLocalPath As String = Nothing
    Friend Shared rendererNextRemoteUri As String = Nothing
    Friend Shared rendererNextMetaData As String = ""
    Friend Shared rendererNextDurationMs As Integer = 0
    ' What we actually handed MusicBee - the file whose arrival identifies the transition.
    Friend Shared rendererQueuedNextFile As String = Nothing
    ' A whole track's playing time is available before the next one is needed, so the wait for its
    ' local copy can be generous - unlike the first play, nobody is listening to silence.
    Private Const rendererNextCopyWaitMs As Integer = 30000

    Private Shared Sub ClearRendererNext()
        rendererNextUri = Nothing
        rendererNextLocalPath = Nothing
        rendererNextRemoteUri = Nothing
        rendererNextMetaData = ""
        rendererNextDurationMs = 0
        rendererQueuedNextFile = Nothing
    End Sub

    ' Called on MusicBee's TrackChanged. When the track that just started is the one we queued, the
    ' gapless transition has happened - so per AVTransport the next URI BECOMES the current one and
    ' the next slot empties. Doing it here means a controller polling GetMediaInfo/GetPositionInfo
    ' sees the truth without having to be told, which is the whole point of the second slot.
    Friend Shared Sub PromoteRendererNextIfPlaying(sourceFileUrl As String)
        If rendererQueuedNextFile Is Nothing OrElse sourceFileUrl Is Nothing Then Exit Sub
        If Not String.Equals(sourceFileUrl, rendererQueuedNextFile, StringComparison.OrdinalIgnoreCase) Then Exit Sub
        Dim playedFromCopy As Boolean = (rendererNextLocalPath Is Nothing AndAlso
                                         Not String.Equals(rendererQueuedNextFile, rendererNextRemoteUri, StringComparison.Ordinal))
        rendererCurrentUri = rendererNextUri
        rendererCurrentLocalPath = rendererNextLocalPath
        rendererCurrentRemoteUri = rendererNextRemoteUri
        rendererCurrentMetaData = rendererNextMetaData
        rendererCurrentDurationMs = rendererNextDurationMs
        ' If the queued file was the downloaded copy we are already ON it, so a later Seek must not
        ' swap again; if it was the remote URL, Seek still has to.
        rendererPlayingCacheFile = If(playedFromCopy, rendererQueuedNextFile, Nothing)
        rendererPendingUri = False
        LogInformation("Renderer:Transition", "next promoted to current: " & If(rendererCurrentUri, "?"))
        ClearRendererNext()
    End Sub

    ' Hands MusicBee the next track, preferring its downloaded copy. Runs off the SOAP thread because
    ' it deliberately WAITS: NowPlayingList_PlayNow REPLACES the now-playing list, so queueing before
    ' the current track has actually started would put the next track into a list about to be thrown
    ' away. So wait for playback to be running, then queue.
    Private Shared Sub RendererQueueNextWorker(state As Object)
        Dim uri As String = TryCast(state, String)
        If uri Is Nothing Then Exit Sub
        Try
            Dim deadline As Long = DateTime.UtcNow.Ticks + (20000L * TimeSpan.TicksPerMillisecond)
            Do While DateTime.UtcNow.Ticks < deadline
                ' A newer SetNextAVTransportURI supersedes this one - drop out silently.
                If Not String.Equals(rendererNextUri, uri, StringComparison.Ordinal) Then Exit Sub
                If Not rendererPendingUri AndAlso mbApiInterface.Player_GetPlayState() = PlayState.Playing Then Exit Do
                Thread.Sleep(200)
            Loop
            If Not String.Equals(rendererNextUri, uri, StringComparison.Ordinal) Then Exit Sub
            Dim target As String = rendererNextLocalPath
            If target Is Nothing Then
                ' Same reasons as the current track: a local copy carries the title and can be sought.
                target = WaitForRendererCache(rendererNextRemoteUri, rendererNextCopyWaitMs, False)
                If target Is Nothing Then target = rendererNextRemoteUri
            End If
            If target Is Nothing Then Exit Sub
            If Not String.Equals(rendererNextUri, uri, StringComparison.Ordinal) Then Exit Sub
            If mbApiInterface.NowPlayingList_QueueNext(target) Then
                rendererQueuedNextFile = target
                LogInformation("Renderer:QueueNext", "queued " & target)
            Else
                LogInformation("Renderer:QueueNext", "MusicBee refused " & target)
            End If
        Catch ex As Exception
            LogError(ex, "Renderer:RendererQueueNextWorker", "uri=" & uri)
        End Try
    End Sub
    ' Diagnostic counters for the position-poll log (see GetPositionInfo). Deliberately unsynchronized:
    ' a lost increment across HTTP threads only changes how often a diagnostic line is written.
    Friend Shared rendererPositionLogBudget As Integer = 0
    Friend Shared rendererPositionPollCount As Integer = 0

End Class
