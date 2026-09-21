Imports System.Text
Imports System.Xml
Imports System.Runtime.InteropServices
Imports System.Reflection
Imports System.Threading
Imports System.Net
Imports System.Net.Sockets

Partial Public Class Plugin
    Friend NotInheritable Class MediaServerDevice
        Inherits UpnpDevice
        Private currentPlayStatsUrl As String = Nothing
        Private skipCountTriggerStartTime As Long
        Private skipCountTriggerEndTime As Long
        Private ReadOnly playStatisticsTimer As New Timer(AddressOf playStatisticsTimer_Tick, Nothing, Timeout.Infinite, Timeout.Infinite)
        Private ReadOnly usedStreamHandle As New HashSet(Of Integer)
        Private Shared requestCounter As Integer = 0

        Public Sub New(udn As Guid)
            MyBase.New(udn)
            Services.Add(New ConnectionManagerService(server))
            Services.Add(New ContentDirectoryService(server))
            Services.Add(New MediaReceiverRegistrarService(server))
            server.HttpServer.AddRoute("HEAD", "/Files/*", New HttpRouteDelegate(AddressOf GetFile))
            server.HttpServer.AddRoute("GET", "/Files/*", New HttpRouteDelegate(AddressOf GetFile))
            server.HttpServer.AddRoute("HEAD", "/Encode/*", New HttpRouteDelegate(AddressOf GetEncodedFile))
            server.HttpServer.AddRoute("GET", "/Encode/*", New HttpRouteDelegate(AddressOf GetEncodedFile))
            server.HttpServer.AddRoute("HEAD", "/Thumbnail/*", New HttpRouteDelegate(AddressOf GetThumbnailFile))
            server.HttpServer.AddRoute("GET", "/Thumbnail/*", New HttpRouteDelegate(AddressOf GetThumbnailFile))
            server.HttpServer.AddRoute("HEAD", "/PodcastThumbnail/*", New HttpRouteDelegate(AddressOf GetPodcastThumbnail))
            server.HttpServer.AddRoute("GET", "/PodcastThumbnail/*", New HttpRouteDelegate(AddressOf GetPodcastThumbnail))
            server.HttpServer.AddRoute("HEAD", "/Web/Images/htmllogo48.png", New HttpRouteDelegate(AddressOf GetWebPng))
            server.HttpServer.AddRoute("GET", "/Web/Images/htmllogo48.png", New HttpRouteDelegate(AddressOf GetWebPng))
            server.HttpServer.AddRoute("HEAD", "/Web/Images/htmllogo64.png", New HttpRouteDelegate(AddressOf GetWebPng))
            server.HttpServer.AddRoute("GET", "/Web/Images/htmllogo64.png", New HttpRouteDelegate(AddressOf GetWebPng))
        End Sub

        Public Sub Dispose()
            [Stop]()
            playStatisticsTimer.Dispose()
            EncodedCache.Clear()
        End Sub

        Public ReadOnly Property HttpServer() As HttpServer
            Get
                Return server.HttpServer
            End Get
        End Property

        Public Overrides Sub Start()
            If Settings.TryPortForwarding Then
                UpnpControlPoint.StartPortForwarding()
            End If
            MyBase.Start()
        End Sub

        Protected Overrides Sub WriteSpecificDescription(writer As XmlTextWriter)
            writer.WriteElementString("dlna", "X_DLNADOC", "urn:schemas-dlna-org:device-1-0", "DMS-1.50")
            writer.WriteStartElement("iconList")
            For Each size As String In New String() {"64", "48"}
                writer.WriteStartElement("icon")
                writer.WriteElementString("mimetype", "image/jpeg")
                writer.WriteElementString("width", size)
                writer.WriteElementString("height", size)
                writer.WriteElementString("depth", "32")
                writer.WriteElementString("url", String.Format("/web/images/htmllogo{0}.png", size))
                writer.WriteEndElement()
            Next size
            writer.WriteEndElement()
        End Sub

        ' F7 - Apply the active profile's content-length policy.
        ' Returns the string to emit, or Nothing if the header should be omitted.
        Private Shared Function FormatContentLength(profile As StreamingProfile, isPcmStream As Boolean, actualLength As Long) As String
            Select Case profile.ContentLength
                Case ContentLengthMode.[None]
                    Return Nothing
                Case ContentLengthMode.PcmOnly
                    If Not isPcmStream Then
                        Return Nothing
                    End If
                    Return actualLength.ToString()
                Case ContentLengthMode.Fixed
                    ' UInt32.MaxValue - 8192 = 4294959103. See LMS for the historical reason for -8192.
                    Return "4294959103"
                Case Else
                    Return actualLength.ToString()
            End Select
        End Function

        Private Sub GetFile(request As HttpRequest)
            Dim filename As String = request.Url.Substring(request.Url.LastIndexOf("/"c) + 1)
            If filename.Length < 19 Then
                LogInformation("GetFile", "Bad filename=" & request.Url)
                Throw New HttpException(404, "Bad parameter")
            End If
            Dim directory As ItemManager = ItemManager.GetItemManager(request.Headers)
            Dim id As String = filename.Substring(0, 16)
            Dim musicBeePlayToMode As Boolean = (filename.Chars(16) = "p"c)
            Dim mime As String = "audio/" & filename.Substring(If(Not musicBeePlayToMode, 17, 18))
            Dim url As String = Nothing
            Dim duration As TimeSpan
            If Not directory.TryGetFileInfo(id, url, duration) Then
                LogInformation("GetFile", "Bad id=" & request.Url)
                Throw New HttpException(404, "Bad parameter")
            End If
            Dim response As HttpResponse = request.Response
            If Not IO.File.Exists(url) Then
                LogInformation("GetFile", "Not found=" & request.Url)
                Throw New HttpException(404, "File not found")
            End If
            Dim counter As Integer = Interlocked.Increment(requestCounter)
            If Settings.LogDebugInfo Then
                Dim localAddress As String = "unknown address"
                Dim remoteAddress As String = "unknown address"
                If TypeOf request.Socket.Client.LocalEndPoint Is IPEndPoint Then
                    localAddress = DirectCast(request.Socket.Client.LocalEndPoint, IPEndPoint).Address.ToString()
                End If
                If TypeOf request.Socket.Client.RemoteEndPoint Is IPEndPoint Then
                    remoteAddress = DirectCast(request.Socket.Client.RemoteEndPoint, IPEndPoint).Address.ToString()
                End If
                LogInformation("GetFile[" & counter & "] " & localAddress, request.Method & " " & url & " to " & remoteAddress)
                ' Same request dump as the encoded path, so the two can be compared directly: what a
                ' client asks for when it is happy (native) against what it asks for when it falls
                ' back to a generic decoder (transcoded), at the same position in a queue.
                Dim askedNative As New StringBuilder()
                For Each header As KeyValuePair(Of String, String) In request.Headers
                    If askedNative.Length > 0 Then askedNative.Append(" | ")
                    askedNative.Append(header.Key).Append("="c).Append(header.Value)
                Next header
                LogInformation("GetFile[" & counter & "].Request", askedNative.ToString())
            End If
            Using stream As New IO.FileStream(url, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read, 65536, IO.FileOptions.SequentialScan)
                Dim fileLength As Long = stream.Length
                Dim range As String = Nothing
                If request.Headers.TryGetValue("range", range) Then
                    Dim values As String() = range.Split("="c).Last().Split("-"c).[Select](Function(a) a.Trim()).ToArray()
                    Dim byteRangeStart As Long = Long.Parse(values(0))
                    Dim byteRangeEnd As Long = byteRangeStart
                    If byteRangeStart < 0 Then
                        byteRangeStart += fileLength
                    End If
                    If values.Length < 2 OrElse Not Long.TryParse(values(1), byteRangeEnd) Then
                        byteRangeEnd = fileLength - 1
                    End If
                    If Settings.LogDebugInfo Then
                        LogInformation("GetFile", "range=" & String.Format("bytes {0}-{1}/{2}", byteRangeStart, byteRangeEnd, fileLength))
                    End If
                    response.AddHeader("Content-Range", String.Format("bytes {0}-{1}/{2}", byteRangeStart, byteRangeEnd, fileLength))
                    fileLength = byteRangeEnd - byteRangeStart + 1
                    response.StateCode = 206
                    stream.Position = byteRangeStart
                End If
                ' F7 - Content-Length per profile policy. Native path: detect PCM-stream by mime ("audio/L16"/"audio/L24" or "audio/wav").
                Dim nativeProfile As StreamingProfile = Settings.GetStreamingProfile(request.Headers)
                Dim nativeIsPcm As Boolean = (mime.StartsWith("audio/L", StringComparison.OrdinalIgnoreCase) OrElse mime.Equals("audio/wav", StringComparison.OrdinalIgnoreCase) OrElse mime.Equals("audio/x-wav", StringComparison.OrdinalIgnoreCase))
                Dim nativeLenStr As String = FormatContentLength(nativeProfile, nativeIsPcm, fileLength)
                If nativeLenStr IsNot Nothing Then
                    response.AddHeader(HttpHeader.ContentLength, nativeLenStr)
                End If
                response.AddHeader(HttpHeader.ContentType, mime)
                response.AddHeader(HttpHeader.AcceptRanges, "bytes")
                response.AddHeader("transferMode.dlna.org", "Streaming")
                response.AddHeader("contentFeatures.dlna.org", directory.GetFileFeature(url, (duration.Ticks <= 0)))
                response.SendHeaders()
                If request.Method = "GET" Then
                    'Dim data(65535) As Byte
                    'Dim dataHandle As GCHandle
                    'dataHandle = GCHandle.Alloc(data, GCHandleType.Pinned)
                    'Do
                    '    Dim count As Integer = stream.Read(data, 0, data.Length)
                    '    Debug.WriteLine(count)
                    '    If count <= 0 Then Exit Do
                    '    If send(request.Socket.Client.Handle, dataHandle.AddrOfPinnedObject, count, 0) = -1 Then
                    '        Debug.WriteLine("err=" & WSAGetLastError())
                    '        Exit Do
                    '    End If
                    '    Thread.Sleep(40)
                    'Loop
                    'Debug.WriteLine("done 1")
                    'shutdown(request.Socket.Client.Handle, 2)
                    'Debug.WriteLine("done 2")
                    'dataHandle.Free()
                    'Exit Sub
                    Dim startTime As Long
                    Dim errorCode As Integer
                    Dim playTime As Long
                    WaitOnSendBarrier("GetFile[" & counter & "]")
                    Try
                        If Not musicBeePlayToMode AndAlso range Is Nothing Then
                            StartPlayStatisticsTriggerTimer(url, duration)
                        End If
                        startTime = DateTime.UtcNow.Ticks
                        errorCode = Sockets_Stream_File(stream.SafeFileHandle.DangerousGetHandle, CUInt(fileLength), request.Socket.Client.Handle)
                        playTime = (DateTime.UtcNow.Ticks - startTime) \ TimeSpan.TicksPerMillisecond
                    Finally
                        ReleaseSendBarrier()
                    End Try
                    If Settings.LogDebugInfo Then
                        LogInformation("GetFile[" & counter & "]", "exit=" & errorCode & ", playtime=" & playTime)
                    End If
                End If
            End Using
        End Sub
        '<DllImport("ws2_32.dll", CharSet:=CharSet.Unicode)> _
        'Private Shared Function send(socketHandle As IntPtr, data As IntPtr, length As Integer, flags As Integer) As Integer
        'End Function
        '<DllImport("ws2_32.dll", CharSet:=CharSet.Unicode)> _
        'Private Shared Function shutdown(socketHandle As IntPtr, flags As Integer) As Integer
        'End Function
        '<DllImport("ws2_32.dll", CharSet:=CharSet.Unicode)> _
        'Private Shared Function WSAGetLastError() As Integer
        'End Function

        Private Sub GetEncodedFile(request As HttpRequest)
            Dim filename As String = request.Url.Substring(request.Url.LastIndexOf("/"c) + 1)
            If filename.Length < 19 Then
                LogInformation("GetEncodedFile", "Bad filename=" & request.Url)
                Throw New HttpException(404, "Bad parameter")
            End If
            Dim extIndex As Integer = filename.LastIndexOf("."c)
            Dim id As String = filename.Substring(0, 16)
            Dim targetMime As String = "audio/" & filename.Substring(extIndex + 1)
            ' Defensive parse - CInt() throws OverflowException when the slice doesn't fit
            ' Int32 (rare but seen with some radio playback URL shapes). TryParse keeps the
            ' HTTP server alive - bad parse = 404 instead of unhandled exception.
            Dim streamHandle As Integer = 0
            If extIndex <> 17 Then
                Dim handleStr As String = filename.Substring(16, extIndex - 16)
                If Not Integer.TryParse(handleStr, streamHandle) Then
                    ' Always-on log entry (LogError writes when DEBUG build OR LogDebugInfo
                    ' is enabled). Captures the full URL + the bad handle slice so we can
                    ' track what's producing these malformed requests.
                    LogError(New ArgumentException("Bad stream handle slice"), "GetEncodedFile.BadHandle", "url=" & request.Url & " handleSlice=""" & handleStr & """ id=""" & id & """ ext=""" & filename.Substring(extIndex + 1) & """")
                    Throw New HttpException(404, "Bad parameter: handle=""" & handleStr & """")
                End If
            End If
            Dim musicBeePlayToMode As Boolean = (streamHandle <> 0)
            If streamHandle <> 0 AndAlso String.Compare(request.Method, "GET", StringComparison.OrdinalIgnoreCase) = 0 Then
                SyncLock usedStreamHandle
                    If Not usedStreamHandle.Add(streamHandle) Then
                        ' stop closed stream handle being re-used for seek
                        streamHandle = 0
                    End If
                End SyncLock
            End If
            Dim encoder As AudioEncoder
            Select Case targetMime
                Case "audio/mpeg", "audio/mp3", "audio/x-mp3"
                    encoder = New AudioEncoder(FileCodec.Mp3)
                Case "audio/m4a", "audio/mp4", "audio/aac", "audio/x-aac"
                    encoder = New AudioEncoder(FileCodec.Aac)
                Case "audio/x-ogg", "audio/ogg"
                    encoder = New AudioEncoder(FileCodec.Ogg)
                Case "audio/flac", "audio/x-flac"
                    ' F9 - without this the FLAC transcode URL fell through to the Else branch and
                    ' the server sent raw L16 PCM bytes under an audio/flac content type.
                    encoder = New AudioEncoder(FileCodec.Flac)
                Case "audio/x-ms-wma", "audio/wma", "audio/x-wma"
                    encoder = New AudioEncoder(FileCodec.Wma)
                Case "audio/wav", "audio/x-wav"
                    encoder = New AudioEncoder(FileCodec.Wave)
                Case Else
                    encoder = New AudioEncoder(FileCodec.Pcm)
            End Select
            Dim directory As ItemManager = ItemManager.GetItemManager(request.Headers)
            Dim isContinuousStream As Boolean = False
            Dim url As String
            Dim duration As TimeSpan
            If id = "continuousstream" Then
                isContinuousStream = True
                url = Nothing
                duration = TimeSpan.Zero
            ElseIf Not directory.TryGetFileInfo(id, url, duration) Then
                LogInformation("GetEncodedFile", "Bad id=" & request.Url)
                Throw New HttpException(404, "Bad parameter")
            ElseIf streamHandle = 0 Then
                ' Per-profile EQ/DSP + ReplayGain. ReplayGain mode comes from MusicBee's current player setting
                ' when the profile opts in; Off otherwise.
                Dim profileForStream As StreamingProfile = Settings.GetStreamingProfile(request.Headers)
                Dim rgMode As ReplayGainMode = If(profileForStream.EnableReplayGain, mbApiInterface.Player_GetReplayGainMode(), ReplayGainMode.Off)
                streamHandle = mbApiInterface.Player_OpenStreamHandle(url, musicBeePlayToMode, profileForStream.EnableSoundEffects, rgMode)
            End If
            If streamHandle = 0 Then
                LogInformation("GetEncodedFile", "Stream zero=" & request.Url)
                Throw New HttpException(404, "File not found")
            Else
                Dim response As HttpResponse = request.Response
                Dim streamingProfile As StreamingProfile = Settings.GetStreamingProfile(request.Headers)
                Dim fileDuration As Double = 0
                Dim fileDecodeStartPos As Long = 0
                Dim fileEncodeLength As Long = 0
                Dim isPartialContent As Boolean = False
                Dim isPcmData As Boolean = (encoder.Codec = FileCodec.Pcm OrElse encoder.Codec = FileCodec.Wave)
                Dim sampleRate As Integer
                Dim channelCount As Integer
                Dim streamCodec As FileCodec
                Dim bitDepth As Integer
                Bass.TryGetStreamInformation(streamHandle, sampleRate, channelCount, streamCodec)
                If isContinuousStream Then
                    fileDuration = 0
                ElseIf duration.Ticks <= 0 Then
                    fileDuration = Bass.GetDecodedDuration(streamHandle)
                    ' Continuous-style streams (radio / icecast / HLS) may report Infinity,
                    ' NaN, or a number large enough to overflow Int64 when multiplied by
                    ' TicksPerSecond (10^7). Without this guard the CLng() throws
                    ' OverflowException - the original "Arithmetic operation resulted in
                    ' an overflow" crash. Treat any non-finite or out-of-range value as
                    ' "unknown duration" → continuous-stream code path downstream.
                    If fileDuration > 0 AndAlso Not Double.IsInfinity(fileDuration) AndAlso Not Double.IsNaN(fileDuration) Then
                        Dim ticksDouble As Double = fileDuration * TimeSpan.TicksPerSecond
                        If ticksDouble < Long.MaxValue AndAlso ticksDouble > 0 Then
                            duration = New TimeSpan(CLng(ticksDouble))
                        Else
                            LogError(New OverflowException("Bass duration out of Int64 range"), "GetEncodedFile.DurationOverflow", "url=" & request.Url & " duration=" & fileDuration.ToString())
                            fileDuration = 0
                        End If
                    Else
                        fileDuration = 0
                    End If
                Else
                    fileDuration = duration.Ticks / TimeSpan.TicksPerSecond
                End If
                If streamingProfile.TranscodeSampleRate <> -1 Then
                    sampleRate = streamingProfile.TranscodeSampleRate
                ElseIf sampleRate < streamingProfile.MinimumSampleRate Then
                    sampleRate = streamingProfile.MinimumSampleRate
                ElseIf sampleRate > streamingProfile.MaximumSampleRate Then
                    sampleRate = streamingProfile.MaximumSampleRate
                End If
                ' F32 - only downmix when the user explicitly asked for stereo via the profile.
                ' Before F32 the condition was `StereoOnly OrElse Not isPcmData`, which silently
                ' downmixed every non-PCM transcode (FLAC, MP3, AAC, Ogg) to stereo regardless of
                ' source channel count - defeating the purpose of having a 5.1-capable renderer
                ' when the source is a 5.1 FLAC. Now the downmix is gated on StereoOnly alone for
                ' FLAC (which natively supports 5.1+). MP3/AAC/Ogg still get force-stereo because
                ' MusicBee's command-line encoders for those formats expect 2-channel input.
                If streamingProfile.StereoOnly _
                    OrElse (Not isPcmData AndAlso encoder.Codec <> FileCodec.Flac) Then
                    channelCount = 2
                End If
                bitDepth = If(Not isPcmData OrElse isContinuousStream, 16, streamingProfile.TranscodeBitDepth)
                Dim sourceStreamHande As Integer = streamHandle
                Dim streamStartPosition As Long = Bass.GetStreamPosition(sourceStreamHande)
                streamHandle = encoder.GetEncodeStreamHandle(sourceStreamHande, sampleRate, channelCount, bitDepth, (musicBeePlayToMode AndAlso Not isContinuousStream))
                Dim counter As Integer = Interlocked.Increment(requestCounter)
                Dim logId As String = "GetEncodedFile[" & counter & "]"
                ' Dump what the client actually asked for. The non-PCM branch below never reads the
                ' `range` header at all - it answers 200 with the whole stream - so a client that
                ' requests one gets no Content-Range and no clue it was ignored, and nothing about
                ' that reaches the log. This line is how we find out whether a queued track asks for
                ' something the first track of a session does not.
                If Settings.LogDebugInfo Then
                    Dim asked As New StringBuilder()
                    For Each header As KeyValuePair(Of String, String) In request.Headers
                        If asked.Length > 0 Then asked.Append(" | ")
                        asked.Append(header.Key).Append("="c).Append(header.Value)
                    Next header
                    LogInformation(logId & ".Request", asked.ToString())
                End If
                ' Encode to a file first, then serve the file, for every format produced by an
                ' external encoder. Two things this buys that piping cannot:
                '   - a real Content-Length and working byte ranges, so a client will hand the
                '     stream to its own native decoder instead of refusing it or falling back to a
                '     generic one (measured: the PCM path, which does send a length, is accepted by
                '     Android's stagefright; the piped FLAC path is not);
                '   - formats whose container cannot be written to a pipe at all, because it has to
                '     seek back to finish its header once the length is known (MP4/AAC).
                ' PCM and Wave are deliberately excluded: BASS encodes those in-process, they
                ' already carry a correct Content-Length, and they work. Nothing here changes them.
                ' Only for a stream with a known duration - a radio stream has no end to wait for.
                If Not isContinuousStream AndAlso fileDuration > 0 AndAlso Not isPcmData _
                    AndAlso RequiresEncodedFile(encoder.Codec) Then
                    ServeFromEncodedFile(request, response, encoder, streamHandle, sourceStreamHande,
                                         url, id, duration, fileDuration, bitDepth, sampleRate,
                                         channelCount, streamingProfile, directory, targetMime, logId)
                    Return
                End If
                If fileDuration <= 0 Then 'duration.Ticks <= 0 Then
                    response.AddHeader("transferMode.dlna.org", "Streaming")
                    response.AddHeader("contentFeatures.dlna.org", directory.GetContinuousStreamFeature(encoder.Codec))
                    response.AddHeader(HttpHeader.AcceptRanges, "none")
                    If isContinuousStream Then
                        fileEncodeLength = 4294967294
                        ' F7 - Even for the "infinite" continuous-stream length, honor the profile's Content-Length policy.
                        Dim contLenStr As String = FormatContentLength(streamingProfile, isPcmData, fileEncodeLength)
                        If contLenStr IsNot Nothing Then
                            response.AddHeader(HttpHeader.ContentLength, contLenStr)
                        End If
                    End If
                Else
                    ' How many bytes BASS will hand the encoder. For PCM/Wave this doubles as the
                    ' response Content-Length, which is why it used to be computed for those only.
                    ' A command-line encoder needs it too: BASS writes this length into the WAV
                    ' header it pipes to the encoder's stdin, and leaving it 0 makes BASS stamp a
                    ' placeholder there - flac.exe copied that placeholder into STREAMINFO, so every
                    ' transcoded FLAC declared ~3h22 and could not be seeked by duration.
                    ' Only for a known duration; a live/radio stream keeps 0 and the placeholder,
                    ' which is correct for something with no end.
                    ' Ask the decoder how long the stream really is rather than trusting the tag:
                    ' MusicBee's duration is rounded to the millisecond, and since this value is a
                    ' hard stop for the encoder, deriving it from the tag chopped the last ~44
                    ' samples off every track - inaudible alone, but it breaks gapless and makes the
                    ' encoded FLAC's STREAMINFO disagree with the source by the same amount.
                    Dim exactDuration As Double = Bass.GetDecodedDuration(sourceStreamHande)
                    Dim decodedLength As Long = Bass.GetDecodedLength(streamHandle, If(exactDuration > 0, exactDuration, fileDuration))
                    If bitDepth <> 24 Then
                        fileEncodeLength = decodedLength \ 2
                    Else
                        fileEncodeLength = (decodedLength * 3) \ 4
                    End If
                    response.AddHeader("X-AvailableSeekRange", String.Format(System.Globalization.CultureInfo.InvariantCulture, "1 npt=0.0-{0:0.000}", duration.TotalSeconds))
                    Dim npt As String
                    If request.Headers.TryGetValue("timeSeekRange.dlna.org", npt) OrElse request.Headers.TryGetValue("npt", npt) Then
                        Dim timeRange As String() = npt.Split("="c).Last().Split("-"c).[Select](Function(a) a.Trim()).ToArray()
                        Dim timeRangeStart As Double = 0
                        If Not Double.TryParse(timeRange(0), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, timeRangeStart) Then
                            Dim startSpan As TimeSpan
                            If TimeSpan.TryParse(timeRange(0), startSpan) Then
                                timeRangeStart = startSpan.TotalSeconds
                            End If
                        End If
                        If timeRangeStart < 0 Then
                            timeRangeStart += duration.TotalSeconds
                        End If
                        Dim timeRangeEnd As Double
                        If timeRange.Length < 2 Then
                            timeRangeEnd = duration.TotalSeconds
                        ElseIf Not Double.TryParse(timeRange(1), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, timeRangeEnd) Then
                            Dim endSpan As TimeSpan
                            If Not TimeSpan.TryParse(timeRange(1), endSpan) Then
                                timeRangeEnd = endSpan.TotalSeconds
                            End If
                        Else
                            timeRangeEnd = duration.TotalSeconds
                        End If
                        response.AddHeader("Vary", "timeSeekRange.dlna.org")
                        response.AddHeader("timeSeekRange.dlna.org", String.Format(System.Globalization.CultureInfo.InvariantCulture, "npt={0:0.000}-{1:0.000}", timeRangeStart, timeRangeEnd)) ''/{2:0.000}", timeRangeStart, timeRangeEnd, duration.TotalSeconds))
                        If Settings.LogDebugInfo Then
                            LogInformation(logId, String.Format(System.Globalization.CultureInfo.InvariantCulture, "npt={0:0.000}-{1:0.000}", timeRangeStart, timeRangeEnd))
                        End If
                        response.AddHeader(HttpHeader.AcceptRanges, "none")
                        response.AddHeader("contentFeatures.dlna.org", directory.GetEncodeFeature(encoder.Codec, False))
                        isPartialContent = (timeRangeStart > 0)
                        fileDecodeStartPos = Bass.GetDecodedLength(streamHandle, timeRangeStart)
                        If isPcmData Then
                            ' shouldnt need to happen for pcm streams but just in case
                            fileEncodeLength = Bass.GetDecodedLength(streamHandle, fileDuration) - fileDecodeStartPos
                            If bitDepth <> 24 Then
                                fileEncodeLength \= 2
                            Else
                                fileEncodeLength = (fileEncodeLength * 3) \ 4
                            End If
                        End If
                    ElseIf Not isPcmData OrElse fileEncodeLength <= 0 Then
                        response.AddHeader(HttpHeader.AcceptRanges, "none")
                        response.AddHeader("contentFeatures.dlna.org", directory.GetEncodeFeature(encoder.Codec, (fileDuration <= 0)))
                        ' F7 - the profile's Content-Length policy never reached this branch, so a
                        ' transcoded stream always went out with no length header at all - which is
                        ' precisely the case the Fixed placeholder exists for, and some clients
                        ' refuse their native decoder for a stream they cannot measure. Only Fixed
                        ' applies here: the encoded size is unknowable until the encode finishes, so
                        ' there is nothing honest to send for the other modes.
                        If streamingProfile.ContentLength = ContentLengthMode.Fixed Then
                            Dim fixedLenStr As String = FormatContentLength(streamingProfile, isPcmData, 0)
                            If fixedLenStr IsNot Nothing Then
                                response.AddHeader(HttpHeader.ContentLength, fixedLenStr)
                            End If
                        End If
                    Else
                        response.AddHeader(HttpHeader.AcceptRanges, "bytes")
                        response.AddHeader("contentFeatures.dlna.org", directory.GetEncodeFeature(encoder.Codec, False))
                        Dim contentLength As Long = fileEncodeLength
                        If encoder.Codec = FileCodec.Wave Then
                            contentLength += 44
                        End If
                        Dim byteRange As String = Nothing
                        If request.Headers.TryGetValue("range", byteRange) Then
                            Dim values As String() = byteRange.Split("="c).Last().Split("-"c).[Select](Function(a) a.Trim()).ToArray()
                            Dim byteRangeStart As Long
                            Dim byteRangeEnd As Long
                            If Not Long.TryParse(values(0), byteRangeStart) Then
                                byteRangeStart = 0
                            ElseIf byteRangeStart < 0 Then
                                byteRangeStart += contentLength
                            End If
                            If values.Length < 2 OrElse Not Long.TryParse(values(1), byteRangeEnd) Then
                                byteRangeEnd = contentLength - 1
                            End If
                            response.StateCode = 206
                            response.AddHeader("Content-Range", String.Format("bytes {0}-{1}/{2}", byteRangeStart, byteRangeEnd, contentLength))
                            If Settings.LogDebugInfo Then
                                LogInformation(logId, "range=" & String.Format("bytes {0}-{1}/{2}", byteRangeStart, byteRangeEnd, contentLength))
                            End If
                            isPartialContent = (byteRangeStart > 0)
                            contentLength = byteRangeEnd - byteRangeStart + 1
                            fileEncodeLength = contentLength
                            If encoder.Codec = FileCodec.Wave AndAlso Not isPartialContent Then
                                ' fileEncodeLength is the PCM budget handed to the encoder; the 44-byte
                                ' RIFF header it writes sits on top of it. A `bytes=0-` request still
                                ' gets the header, so the budget has to exclude those 44 bytes or we
                                ' send 44 more than the Content-Range we just promised.
                                fileEncodeLength -= 44
                            End If
                            If encoder.Codec = FileCodec.Wave AndAlso byteRangeStart > 0 Then
                                byteRangeStart -= 44
                            End If
                            If bitDepth <> 24 Then
                                fileDecodeStartPos = byteRangeStart * 2
                            Else
                                fileDecodeStartPos = (byteRangeStart * 4) \ 3
                            End If
                        End If
                        ' F7 - Encoded-file path Content-Length per profile policy.
                        Dim encContLenStr As String = FormatContentLength(streamingProfile, isPcmData, contentLength)
                        If encContLenStr IsNot Nothing Then
                            response.AddHeader(HttpHeader.ContentLength, encContLenStr)
                        End If
                    End If
                    response.AddHeader("transferMode.dlna.org", "Streaming")
                End If
                If encoder.Codec = FileCodec.Pcm Then
                    targetMime = "audio/L" & If(bitDepth <> 24, "16", "24") & ";rate=" & sampleRate & ";channels=" & channelCount
                End If
                response.AddHeader(HttpHeader.ContentType, targetMime)
                If Settings.LogDebugInfo Then
                    Dim localAddress As String = "unknown address"
                    Dim remoteAddress As String = "unknown address"
                    If TypeOf request.Socket.Client.LocalEndPoint Is IPEndPoint Then
                        localAddress = DirectCast(request.Socket.Client.LocalEndPoint, IPEndPoint).Address.ToString()
                    End If
                    If TypeOf request.Socket.Client.RemoteEndPoint Is IPEndPoint Then
                        remoteAddress = DirectCast(request.Socket.Client.RemoteEndPoint, IPEndPoint).Address.ToString()
                    End If
                    LogInformation(logId & " " & localAddress, request.Method & " " & url & " to " & remoteAddress & "; mime=" & targetMime & ",rate=" & sampleRate & ",channels=" & channelCount)
                End If
                response.SendHeaders()
                If String.Compare(request.Method, "GET", StringComparison.OrdinalIgnoreCase) = 0 Then
                    If fileDecodeStartPos > 0 Then
                        Bass.SetEncodeStreamPosition(sourceStreamHande, streamStartPosition + fileDecodeStartPos)
                    ElseIf Not musicBeePlayToMode Then
                        StartPlayStatisticsTriggerTimer(url, duration)
                    End If
                    ' F5 - pass the profile's little-endian preference to the encoder.
                    encoder.StartEncode(url, streamHandle, isPartialContent, fileEncodeLength, bitDepth, request.Socket.Client.Handle, logId, streamingProfile.ForceLittleEndianPcm)
                End If
            End If
        End Sub

        Private Sub StartPlayStatisticsTriggerTimer(url As String, duration As TimeSpan)
            If Not Settings.ServerUpdatePlayStatistics Then
                currentPlayStatsUrl = Nothing
                playStatisticsTimer.Change(Timeout.Infinite, Timeout.Infinite)
            Else
                If currentPlayStatsUrl IsNot Nothing AndAlso String.Compare(url, currentPlayStatsUrl, StringComparison.OrdinalIgnoreCase) <> 0 AndAlso DateTime.UtcNow.Ticks >= skipCountTriggerStartTime AndAlso DateTime.UtcNow.Ticks < skipCountTriggerEndTime Then
                    ' increment skip count
                    mbApiInterface.Player_UpdatePlayStatistics(currentPlayStatsUrl, PlayStatisticType.IncreaseSkipCount, False)
                End If
                ' Clamp the trigger-time math against overflow. For radio / streaming sources
                ' duration may legitimately be huge or zero - neither should crash the timer.
                Dim minPlayTimeMsLong As Double = duration.Ticks / TimeSpan.TicksPerMillisecond * playCountTriggerPercent
                Dim minPlayTimeMs As Integer
                If minPlayTimeMsLong >= Integer.MaxValue Then
                    minPlayTimeMs = Integer.MaxValue
                ElseIf minPlayTimeMsLong <= 0 Then
                    minPlayTimeMs = 0
                Else
                    minPlayTimeMs = CInt(minPlayTimeMsLong)
                End If
                If playCountTriggerSeconds > 0 AndAlso playCountTriggerSeconds * 1000 < minPlayTimeMs Then
                    minPlayTimeMs = playCountTriggerSeconds * 1000
                End If
                Dim maxSkipTicksDouble As Double = duration.Ticks * skipCountTriggerPercent
                Dim maxSkipTimeTicks As Long
                If maxSkipTicksDouble >= Long.MaxValue Then
                    maxSkipTimeTicks = Long.MaxValue
                ElseIf maxSkipTicksDouble <= 0 Then
                    maxSkipTimeTicks = 0
                Else
                    maxSkipTimeTicks = CLng(maxSkipTicksDouble)
                End If
                If skipCountTriggerSeconds * TimeSpan.TicksPerSecond > maxSkipTimeTicks Then
                    maxSkipTimeTicks = skipCountTriggerSeconds * TimeSpan.TicksPerSecond
                End If
                skipCountTriggerEndTime = DateTime.UtcNow.Ticks + maxSkipTimeTicks
                skipCountTriggerStartTime = DateTime.UtcNow.Ticks + 1500 * TimeSpan.TicksPerMillisecond
                currentPlayStatsUrl = url
                playStatisticsTimer.Change(minPlayTimeMs, Timeout.Infinite)
            End If
        End Sub

        Private Sub playStatisticsTimer_Tick(state As Object)
            If currentPlayStatsUrl IsNot Nothing Then
                ' increment playcount
                mbApiInterface.Player_UpdatePlayStatistics(currentPlayStatsUrl, PlayStatisticType.IncreasePlayCount, False)
                currentPlayStatsUrl = Nothing
            End If
        End Sub

        Private Sub GetThumbnailFile(request As HttpRequest)
            Dim filename As String = request.Url.Substring(request.Url.LastIndexOf("/"c) + 1)
            If filename.Length <> 24 Then
                Throw New HttpException(404, "Bad parameter")
            End If
            Dim id As String = filename.Substring(0, 16)
            Dim size As String = filename.Substring(17, 7)
            Dim directory As ItemManager = ItemManager.GetItemManager(request.Headers)
            Dim url As String
            If Not directory.TryGetThumbnailFile(id, url) Then
                Throw New HttpException(404, "Bad parameter")
            End If
            Dim encoder As ImageEncoder = ImageEncoder.TryCreate("jpeg", size)
            If encoder Is Nothing Then
                Throw New HttpException(404, "Bad parameter")
            End If
            Dim response As HttpResponse = request.Response
            response.AddHeader(HttpHeader.ContentType, encoder.GetMime())
            response.AddHeader("contentFeatures.dlna.org", "DLNA.ORG_PN=JPEG_TN;DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=00D00000000000000000000000000000")
            response.SendHeaders()
            If request.Method = "GET" Then
                encoder.StartEncode(response.Stream, url)
            End If
        End Sub

        ' yaiol - Podcast subscription artwork via Podcasts_GetSubscriptionArtwork. The
        ' library artwork API (Library_GetArtworkUrl) returns "0" for synthesised podcast
        ' urls because MB's library doesn't index them; this route is what the lazy DIDL
        ' emitter points <upnp:albumArtURI> at when the album represents a podcast
        ' subscription. Path: /PodcastThumbnail/<url-encoded subId>. Serves raw JPEG
        ' bytes; the client (BubbleUPnP) scales as needed. No size negotiation -
        ' subscription artwork is typically a single fixed-resolution image and we don't
        ' want to add a re-encode hop just to honour the JPEG_TN/JPEG_SM hint.
        Private Sub GetPodcastThumbnail(request As HttpRequest)
            Dim segment As String = request.Url.Substring(request.Url.LastIndexOf("/"c) + 1)
            If String.IsNullOrEmpty(segment) Then
                Throw New HttpException(404, "Bad parameter")
            End If
            ' The URL is lowercased+unescaped by the HTTP layer, so what we get here is
            ' the slug we minted in ItemManager.PodcastSlug (the last path segment of
            ' the real subId, lowercased). Look up the real subId.
            Dim slug As String = Uri.UnescapeDataString(segment)
            Dim subId As String = Nothing
            If Not ItemManager.podcastSubIdBySlug.TryGetValue(slug, subId) OrElse String.IsNullOrEmpty(subId) Then
                LogInformation("LazyQuery", "[PodcastThumbnail] slug=" & slug & " → no matching subscription, 404")
                Throw New HttpException(404, "Unknown subscription")
            End If
            ' Resolution chain (this MB build has Podcasts_GetSubscriptionArtwork half-
            ' implemented - empirically returns False even when MB shows artwork in its
            ' own UI). Try, in order:
            '   1. Podcasts_GetSubscriptionArtwork with index -1, 0, 1 - covers any
            '      undocumented index convention this build uses.
            '   2. Library_GetArtworkUrl(feedUrl, -2) - the standard library artwork
            '      path; MB keeps subscription artwork keyed against the feed URL.
            ' If we get a Library_GetArtworkUrl filepath, we read it raw and serve.
            Dim imageData() As Byte = Nothing
            Dim winningPath As String = "none"
            ' MB's own subscription artwork cache. The desktop UI loads from
            ' %LocalAppData%\MusicBee\InternalCache\Subscriptions\<subName>.jpg,
            ' so when MB shows artwork for a subscription this file exists.
            ' Most reliable source on this build - Podcasts_GetSubscriptionArtwork
            ' returns False even when the cached JPEG is sitting right there.
            Dim subName As String = Nothing
            If ItemManager.podcastSubNameBySubId.TryGetValue(subId, subName) AndAlso Not String.IsNullOrEmpty(subName) Then
                Try
                    Dim cacheRoot As String = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBee\InternalCache\Subscriptions")
                    Dim cachePath As String = IO.Path.Combine(cacheRoot, subName & ".jpg")
                    If IO.File.Exists(cachePath) Then
                        imageData = IO.File.ReadAllBytes(cachePath)
                        winningPath = "InternalCache\Subscriptions\<name>.jpg"
                    End If
                Catch ex As Exception
                    LogError(ex, "GetPodcastThumbnail.InternalCache", "subId=" & subId & " subName=" & subName)
                End Try
            End If
            For Each idx As Integer In New Integer() {-1, 0, 1}
                If imageData IsNot Nothing Then Exit For
                Try
                    Dim try1() As Byte = Nothing
                    If mbApiInterface.Podcasts_GetSubscriptionArtwork(subId, idx, try1) AndAlso try1 IsNot Nothing AndAlso try1.Length > 0 Then
                        imageData = try1
                        winningPath = "Podcasts_GetSubscriptionArtwork[" & idx & "]"
                        Exit For
                    End If
                Catch
                End Try
            Next
            If imageData Is Nothing Then
                Dim feedUrl As String = Nothing
                If ItemManager.podcastFeedUrlBySubId.TryGetValue(subId, feedUrl) AndAlso Not String.IsNullOrEmpty(feedUrl) Then
                    Try
                        Dim libUrl As String = mbApiInterface.Library_GetArtworkUrl(feedUrl, -2)
                        If Not String.IsNullOrEmpty(libUrl) AndAlso libUrl <> "0" Then
                            If IO.File.Exists(libUrl) Then
                                imageData = IO.File.ReadAllBytes(libUrl)
                                winningPath = "Library_GetArtworkUrl(feedUrl,-2)→file"
                            End If
                        End If
                    Catch ex As Exception
                        LogError(ex, "GetPodcastThumbnail.LibraryFallback", "subId=" & subId & " feedUrl=" & feedUrl)
                    End Try
                End If
            End If
            ' On-disk subscription folder. yaiol convention (not MB-mandated): drop
            ' a folder.jpg / cover.jpg next to the downloaded episodes - that's where
            ' the user maintains podcast artwork outside MB's half-broken Podcasts_-
            ' GetSubscriptionArtwork API. See podcastFolderBySubId in ItemManager for
            ' the path derivation rationale.
            If imageData Is Nothing Then
                Dim folder As String = Nothing
                If ItemManager.podcastFolderBySubId.TryGetValue(subId, folder) AndAlso Not String.IsNullOrEmpty(folder) AndAlso IO.Directory.Exists(folder) Then
                    Try
                        Dim candidates As String() = New String() {"folder.jpg", "folder.jpeg", "folder.png", "cover.jpg", "cover.jpeg", "cover.png", "albumart.jpg", "albumart.png"}
                        For Each name As String In candidates
                            Dim p As String = IO.Path.Combine(folder, name)
                            If IO.File.Exists(p) Then
                                imageData = IO.File.ReadAllBytes(p)
                                winningPath = "diskFolder/" & name
                                Exit For
                            End If
                        Next
                        If imageData Is Nothing Then
                            Dim jpgs() As String = IO.Directory.GetFiles(folder, "*.jpg")
                            If jpgs Is Nothing OrElse jpgs.Length = 0 Then
                                jpgs = IO.Directory.GetFiles(folder, "*.png")
                            End If
                            If jpgs IsNot Nothing AndAlso jpgs.Length > 0 Then
                                imageData = IO.File.ReadAllBytes(jpgs(0))
                                winningPath = "diskFolder/firstImage=" & IO.Path.GetFileName(jpgs(0))
                            End If
                        End If
                    Catch ex As Exception
                        LogError(ex, "GetPodcastThumbnail.DiskFolder", "subId=" & subId & " folder=" & folder)
                    End Try
                End If
            End If
            If imageData Is Nothing OrElse imageData.Length = 0 Then
                LogInformation("LazyQuery", "[PodcastThumbnail] subId=" & subId & " path=" & winningPath & " → 404")
                Throw New HttpException(404, "No artwork")
            End If
            LogInformation("LazyQuery", "[PodcastThumbnail] subId=" & subId & " path=" & winningPath & " bytes=" & imageData.Length)
            Dim response As HttpResponse = request.Response
            response.AddHeader(HttpHeader.ContentType, "image/jpeg")
            response.AddHeader(HttpHeader.ContentLength, imageData.Length.ToString())
            response.AddHeader("contentFeatures.dlna.org", "DLNA.ORG_PN=JPEG_TN;DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=00D00000000000000000000000000000")
            response.SendHeaders()
            If request.Method = "GET" Then
                response.Stream.Write(imageData, 0, imageData.Length)
            End If
        End Sub

        Private Sub GetWebPng(request As HttpRequest)
            Dim response As HttpResponse = request.Response
            Dim name As String = request.Url.Split(New Char() {"/"c}, StringSplitOptions.RemoveEmptyEntries).Last()
            Dim resourceManager As New System.Resources.ResourceManager("MusicBeePlugin.Images", System.Reflection.Assembly.GetExecutingAssembly())
            Using resourceStream As IO.Stream = resourceManager.GetStream(name.Substring(0, name.Length - 4))
                response.AddHeader(HttpHeader.ContentLength, resourceStream.Length.ToString())
                response.AddHeader(HttpHeader.ContentType, "image/png")
                response.SendHeaders()
                If request.Method = "GET" Then
                    resourceStream.CopyTo(response.Stream)
                End If
            End Using
            resourceManager.ReleaseAllResources()
        End Sub

        ''' <summary>
        ''' The formats that have a demonstrated reason to be encoded to a file first. Deliberately
        ''' NOT every externally-encoded format:
        '''   - Aac  - its MP4 container cannot be written to a pipe at all (it seeks back to
        '''            finalise the header), so piping produces a zero-byte stream. Measured.
        '''   - Flac - pipes fine, but with no Content-Length a client will not give it to its own
        '''            decoder: BubbleUPnP falls back to ffmpeg, and a renderer with no fallback may
        '''            refuse it outright. Measured against the PCM path, which does send a length
        '''            and IS accepted natively.
        ''' Mp3 and Ogg stream correctly today with no reported problem, and PCM/Wave are encoded
        ''' in-process with a length that is plain arithmetic. None of them are touched. The same
        ''' argument as FLAC's could be made for Mp3 and Ogg, but it is an argument, not evidence -
        ''' add them when something actually fails, not before.
        ''' </summary>
        Private Shared Function RequiresEncodedFile(codec As FileCodec) As Boolean
            Select Case codec
                Case FileCodec.Aac, FileCodec.Flac
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        ''' <summary>
        ''' Encode the whole track to a temporary file, then serve that file like any other: real
        ''' Content-Length, real byte ranges, real seeking.
        '''
        ''' The cost is that the first byte leaves only once encoding finishes - measured at roughly
        ''' half a second for a four-minute track, since the encoders run far faster than realtime.
        ''' What it buys is that the response stops being an unmeasurable stream, which is what makes
        ''' clients refuse it or fall back to a generic decoder.
        ''' </summary>
        Private Shared Sub ServeFromEncodedFile(request As HttpRequest, response As HttpResponse,
                                                encoder As AudioEncoder, streamHandle As Integer,
                                                sourceStreamHandle As Integer, url As String, id As String,
                                                duration As TimeSpan, fileDuration As Double,
                                                bitDepth As Integer, sampleRate As Integer,
                                                channelCount As Integer, streamingProfile As StreamingProfile,
                                                directory As ItemManager, targetMime As String, logId As String)
            ' Same PCM budget the streaming path computes: the decoder's own length rather than
            ' the tag duration, which is rounded to the millisecond and would clip the tail.
            Dim exactDuration As Double = Bass.GetDecodedDuration(sourceStreamHandle)
            Dim decodedLength As Long = Bass.GetDecodedLength(streamHandle, If(exactDuration > 0, exactDuration, fileDuration))
            Dim encodeBudget As Long = If(bitDepth <> 24, decodedLength \ 2, (decodedLength * 3) \ 4)

            ' Everything that can change the bytes goes in the key. The command line is in there
            ' because it is user-editable in MusicBee's preferences: edit the quality and the
            ' cached file must stop being a match rather than quietly outlive the change.
            Dim keyQuality As EncodeQuality
            Dim keyCommandLine As String = AudioEncoder.GetConvertCommandLine(encoder.Codec, keyQuality, logId)
            Dim cacheKey As String = String.Join("|", id, encoder.Codec.ToString(), sampleRate.ToString(),
                                                 channelCount.ToString(), bitDepth.ToString(),
                                                 If(keyCommandLine, ""))

            Dim encodedPath As String = EncodedCache.GetOrCreate(cacheKey, logId,
                Sub(target As String)
                    Dim startTicks As Long = DateTime.UtcNow.Ticks
                    encoder.StartEncode(url, streamHandle, False, encodeBudget, bitDepth,
                                        request.Socket.Client.Handle, logId, streamingProfile.ForceLittleEndianPcm,
                                        target)
                    Dim encodeMs As Long = (DateTime.UtcNow.Ticks - startTicks) \ TimeSpan.TicksPerMillisecond
                    Dim written As Long = If(IO.File.Exists(target), New IO.FileInfo(target).Length, 0L)
                    LogInformation(logId, "encoded to file: " & written & " bytes in " & encodeMs & " ms")
                End Sub)

            If encodedPath Is Nothing Then
                ' The encoder produced nothing. Saying so is the whole point of this branch
                ' existing - the old behaviour was a valid, empty 200 that told the user nothing.
                LogError(New InvalidOperationException("encoder produced no file"), logId,
                         "codec=" & encoder.Codec.ToString())
                Throw New HttpException(500, "Encoder produced no output")
            End If

            ' FileShare.Delete matters: it lets the cache evict this file while we are still sending
            ' it. Windows drops the name immediately and frees the data when the last handle closes,
            ' so an eviction can never interrupt a response in flight.
            Using stream As New IO.FileStream(encodedPath, IO.FileMode.Open, IO.FileAccess.Read,
                                              IO.FileShare.Read Or IO.FileShare.Delete,
                                              65536, IO.FileOptions.SequentialScan)
                Dim encodedLength As Long = stream.Length
                Dim sendLength As Long = encodedLength
                    Dim byteRange As String = Nothing
                    If request.Headers.TryGetValue("range", byteRange) Then
                        Dim values() As String = byteRange.Split("="c).Last().Split("-"c).[Select](Function(a) a.Trim()).ToArray()
                        Dim rangeStart As Long
                        If Not Long.TryParse(values(0), rangeStart) Then
                            rangeStart = 0
                        ElseIf rangeStart < 0 Then
                            rangeStart += encodedLength
                        End If
                        Dim rangeEnd As Long
                        If values.Length < 2 OrElse Not Long.TryParse(values(1), rangeEnd) Then
                            rangeEnd = encodedLength - 1
                        End If
                        If rangeEnd >= encodedLength Then rangeEnd = encodedLength - 1
                        If rangeStart < 0 Then rangeStart = 0
                        If rangeStart <= rangeEnd Then
                            response.StateCode = 206
                            response.AddHeader("Content-Range", String.Format("bytes {0}-{1}/{2}", rangeStart, rangeEnd, encodedLength))
                            sendLength = rangeEnd - rangeStart + 1
                            stream.Position = rangeStart
                            If Settings.LogDebugInfo Then
                                LogInformation(logId, "range=" & String.Format("bytes {0}-{1}/{2}", rangeStart, rangeEnd, encodedLength))
                            End If
                        End If
                    End If

                    response.AddHeader("X-AvailableSeekRange", String.Format(System.Globalization.CultureInfo.InvariantCulture,
                                                                            "1 npt=0.0-{0:0.000}", duration.TotalSeconds))
                    response.AddHeader(HttpHeader.ContentType, targetMime)
                    response.AddHeader(HttpHeader.AcceptRanges, "bytes")
                    response.AddHeader("transferMode.dlna.org", "Streaming")
                    ' Seeking is genuinely available now, so stop advertising otherwise.
                    response.AddHeader("contentFeatures.dlna.org", directory.GetEncodeFeature(encoder.Codec, False))
                    response.AddHeader(HttpHeader.ContentLength, sendLength.ToString())
                    response.SendHeaders()

                    If String.Compare(request.Method, "GET", StringComparison.OrdinalIgnoreCase) = 0 Then
                        Dim errorCode As Integer
                        Dim sendTicks As Long
                        WaitOnSendBarrier(logId)
                        Try
                            sendTicks = DateTime.UtcNow.Ticks
                            errorCode = Sockets_Stream_File(stream.SafeFileHandle.DangerousGetHandle,
                                                            CUInt(sendLength), request.Socket.Client.Handle)
                        Finally
                            ReleaseSendBarrier()
                        End Try
                        If Settings.LogDebugInfo Then
                            LogInformation(logId, "sent " & sendLength & " bytes, exit=" & errorCode &
                                           ", playtime=" & ((DateTime.UtcNow.Ticks - sendTicks) \ TimeSpan.TicksPerMillisecond))
                        End If
                    End If
            End Using
        End Sub

        <DllImport("MusicBeeBass.dll", CallingConvention:=CallingConvention.Cdecl)> _
        Private Shared Function Sockets_Stream_File(fileHandle As IntPtr, limit As UInteger, socketHandle As IntPtr) As Integer
        End Function
    End Class  ' MediaServerDevice
End Class