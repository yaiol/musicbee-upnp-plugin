' app-icon tag for icons-cockpit (do not remove): data-icon="yaiol:musicbee-upnp-plugin" -> res/icons/custom/apps/musicbee-upnp-plugin.svg
Imports System.Runtime.InteropServices
Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Threading
Imports System.Xml.Serialization

Public Class Plugin
    Friend Shared mbApiInterface As New MusicBeeApiInterface
    Private Shared ReadOnly about As New PluginInfo
    Private Const musicBeePluginVersion As String = "1.0"
    Private Shared startTimeTicks As Long
    Friend Shared server As MediaServerDevice
    Friend Shared controller As ControlPointManager
    Private Shared ReadOnly networkLock As New Object
    Private Shared ReadOnly logLock As New Object
    Private Shared ReadOnly errorCount As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
    ' F45 - concurrent stream cap. Original was a hardcoded 4 which throttled devices that fire
    ' parallel requests (some Marantz/Linn during artwork scans, BubbleUPnP's metadata probes
    ' alongside active playback). Now user-configurable via Settings.MaxConnections (default 16).
    ' WaitOnSendBarrier() warns in the log when the cap is reached so a wedged device leaves a
    ' breadcrumb. The semaphore is built once from the setting at type-init time; changing the
    ' setting requires a MusicBee restart.
    ' Built in Initialise() AFTER Settings is loaded - referencing Settings from a field
    ' initializer fires its shared constructor before mbApiInterface is populated, which throws
    ' at plugin load time ("Unable to initialise plugin").
    Private Shared sendDataBarrier As SemaphoreSlim

    ' F45 - sticky flag set the first time the max-connections cap is hit this session.
    ' Surfaced as a visible red badge in the Settings dialog so non-tech users see it without
    ' reading the log. Stays True until MusicBee restart (matches the semaphore's lifecycle).
    Public Shared MaxConnectionsHit As Boolean = False

    ' Snapshot of the settings that were in effect when the plugin was initialised. Used to
    ' detect "user saved a setting that needs a restart to take effect" - compared in the
    ' Settings dialog Save handler against the freshly-persisted values. Any mismatch flips
    ' Plugin.RestartRequired = True, which lights up the ⚠ Restart Required badge.
    ' List is intentionally short: only settings that genuinely cannot be hot-applied (HTTP
    ' server bind params, the SemaphoreSlim built once at Initialise). Live-applied settings
    ' like profiles, transcoding flags, etc. are NOT here.
    Public Shared activeMaxConnections As Integer = 16
    Public Shared activeServerPort As Integer = 9779
    ' F2.01 - snapshot of EnableMediaRenderer at startup; toggling it needs a MusicBee restart
    ' (the renderer device is advertised once at server construction), so it drives RestartRequired.
    Public Shared activeEnableMediaRenderer As Boolean = False
    ' The port the HTTP server actually bound. Equals activeServerPort normally, but when the
    ' configured port is unavailable HttpServer.Start falls back to the next free port and records
    ' it here. Everything that ADVERTISES the server (SSDP LOCATION, device URL, router port-forward,
    ' self-filtering) must use this, not Settings.ServerPort - clients discover the real port via SSDP.
    Public Shared boundServerPort As Integer = 9779
    Public Shared activeIpAddress As String = ""
    Public Shared RestartRequired As Boolean = False

    Private Shared Sub WaitOnSendBarrier(logTag As String)
        Dim barrier As SemaphoreSlim = sendDataBarrier
        If barrier Is Nothing Then Return  ' Initialise() hasn't built it yet - race on plugin startup
        If barrier.CurrentCount = 0 Then
            MaxConnectionsHit = True
            LogInformation("MaxConnections", logTag & " - all " & Settings.MaxConnections & " concurrent stream slots in use; waiting")
        End If
        barrier.Wait()
    End Sub

    Private Shared Sub ReleaseSendBarrier()
        Dim barrier As SemaphoreSlim = sendDataBarrier
        If barrier IsNot Nothing Then barrier.Release()
    End Sub

    Private Shared ignoreNamePrefixes() As String = New String() {}
    Private Shared ignoreNameChars As String = Nothing
    Private Shared playCountTriggerPercent As Double
    Private Shared playCountTriggerSeconds As Integer
    Private Shared skipCountTriggerPercent As Double
    Private Shared skipCountTriggerSeconds As Integer
    Private Shared logCounter As Integer = 0
    Friend Shared hostAddresses() As IPAddress
    Private Shared subnetMasks()() As Byte
    Friend Shared ipOverrideAddressMatched As Boolean
    Private Shared defaultHost As String
    Private Shared localIpAddresses()() As Byte

    Public Function Initialise(ByVal apiInterfacePtr As IntPtr) As PluginInfo
        CopyMemory(mbApiInterface, apiInterfacePtr, Marshal.SizeOf(mbApiInterface))
        ' i18n - detect MusicBee's UI language and load the matching string bundle before any resource
        ' lookup runs. The plugin always follows MusicBee's language (no separate plugin-side override).
        Localisation.Apply()
        ' F45 - build the concurrent-stream semaphore. The Settings.MaxConnections read below is the
        ' first Settings access, which triggers its shared constructor (loads UPnPSettings.ini). MUST
        ' be BEFORE the HTTP server starts accepting requests (ReceiveNotification → PluginStartup, later).
        sendDataBarrier = New SemaphoreSlim(If(Settings.MaxConnections > 0, Settings.MaxConnections, 16))
        ' Snapshot restart-required settings so the dialog can detect if the user saves a new
        ' value that needs a MusicBee restart to take effect. Done here, not in Settings's shared
        ' constructor, because the snapshot is "what the running plugin is actually using" - and
        ' that's decided here at Initialise time.
        activeMaxConnections = If(Settings.MaxConnections > 0, Settings.MaxConnections, 16)
        activeServerPort = Settings.ServerPort
        ' F2.01 - sweep any local copies of remote casts left behind by a MusicBee that was killed
        ' mid-cast. They are pure scratch; the renderer re-fetches whatever it needs.
        ClearRendererCacheFolder()
        boundServerPort = Settings.ServerPort
        activeEnableMediaRenderer = Settings.EnableMediaRenderer
        activeIpAddress = Settings.IpAddress
        about.PluginInfoVersion = PluginInfoVersion
        ' Product name + fork tag. ⚠ CLAUDE: this ONE display name carries " (yaiol)" because MusicBee's
        ' plugin manager lists rival forks (the original + the 2025 fork) side by side, so it needs
        ' disambiguation. Everywhere our plugin stands alone (website card, settings-window title) uses the
        ' bare ProductName() with NO tag. The product name itself never contains "yaiol" (all of this is yaiol).
        about.Name = UpdateCheck.ProductName() & " (yaiol)"
        about.Description = Plugin.L("PluginDescription")
        about.Author = "Steven Mayall"
        about.TargetApplication = ""
        about.Type = PluginType.Upnp
        ' ⚠ CLAUDE: the version MusicBee shows in Preferences → Plugins - source it from
        ' AssemblyFileVersion (via UpdateCheck.CurrentVersion, which /git bumps) so it tracks every
        ' release. Do NOT hardcode it again (it used to read 1.0.1 while the real build was 2.x.x).
        Dim pluginVer As New Version(UpdateCheck.CurrentVersion())
        about.VersionMajor = CShort(pluginVer.Major)
        about.VersionMinor = CShort(pluginVer.Minor)
        about.Revision = CShort(pluginVer.Build)
        about.MinInterfaceVersion = MinInterfaceVersion
        about.MinApiRevision = MinApiRevision
        about.ReceiveNotifications = (ReceiveNotificationFlags.TagEvents Or ReceiveNotificationFlags.PlayerEvents)
        about.ConfigurationPanelHeight = 0
        Return about
    End Function

    Public Function Configure(ByVal panelHandle As IntPtr) As Boolean
        Using dialog As New SettingsDialog
            dialog.ShowDialog(Form.FromHandle(mbApiInterface.MB_GetWindowHandle()))
        End Using
        Return True
    End Function

    Public Sub SaveSettings()
    End Sub

    Public Sub Close(ByVal reason As PluginCloseReason)
        RemoveHandler NetworkChange.NetworkAddressChanged, AddressOf NetworkChange_NetworkAddressChanged
        Dim closeThread As New Thread(AddressOf ExecuteClose) With {
            .IsBackground = True
        }
        closeThread.Start()
    End Sub

    Private Sub ExecuteClose()
        If controller IsNot Nothing Then
            Try
                controller.Dispose()
            Catch
            End Try
        End If
        If server IsNot Nothing Then
            Try
                server.Dispose()
            Catch
            End Try
        End If
    End Sub

    Public Sub Uninstall()
    End Sub

    Public Sub ReceiveNotification(ByVal sourceFileUrl As String, ByVal type As NotificationType)
        ' F38 - final safety net. Notifications from MusicBee (PlayStateChanged, VolumeChanged, etc.)
        ' dispatch into ControlPointManager which talks to the renderer over SOAP. If the renderer
        ' has died in the meantime, individual methods catch SocketException already - but a top-level
        ' wrapper here ensures NO exception can ever propagate back into MusicBee's notification
        ' pump (which would surface as a generic "TargetInvocationException" popup to the user).
        Try
            ReceiveNotificationInternal(sourceFileUrl, type)
        Catch ex As Exception
            LogError(ex, "ReceiveNotification:" & type.ToString())
        End Try
    End Sub

    Private Sub ReceiveNotificationInternal(ByVal sourceFileUrl As String, ByVal type As NotificationType)
        Select Case type
            Case NotificationType.PluginStartup
                ' Optional log-truncate at every plugin load - keeps UpnpErrorLog.dat
                ' focused on the current session. Gated by Settings.ClearLogOnStartup.
                If Settings.ClearLogOnStartup Then
                    Try
                        Dim logUrl As String = mbApiInterface.Setting_GetPersistentStoragePath() & "UpnpErrorLog.dat"
                        If IO.File.Exists(logUrl) Then
                            IO.File.WriteAllText(logUrl, "")
                        End If
                    Catch
                        ' Best-effort. If the file is locked or unwritable, ignore - don't let
                        ' a log-housekeeping failure block plugin startup.
                    End Try
                End If
                ' SettingId values 4-9 are undocumented in the public SDK - our enum values
                ' are best-guess from the legacy fork. If MusicBee doesn't recognise them,
                ' Setting_GetValue returns False and leaves `value` Nothing. Unboxing Nothing
                ' to Integer would NRE, killing the rest of PluginStartup (notably
                ' GetNetworkAddresses), which then breaks the dialog. Check both the return
                ' AND the value before unboxing, defaulting to safe values on failure.
                Dim value As Object = Nothing
                If mbApiInterface.Setting_GetValue(SettingId.IgnoreNamePrefixes, value) AndAlso TypeOf value Is String() Then
                    ignoreNamePrefixes = DirectCast(value, String())
                End If
                value = Nothing
                If mbApiInterface.Setting_GetValue(SettingId.IgnoreNameChars, value) AndAlso TypeOf value Is String Then
                    ignoreNameChars = DirectCast(value, String)
                End If
                value = Nothing
                If mbApiInterface.Setting_GetValue(SettingId.PlayCountTriggerPercent, value) AndAlso TypeOf value Is Integer Then
                    playCountTriggerPercent = DirectCast(value, Integer) / 100
                End If
                value = Nothing
                If mbApiInterface.Setting_GetValue(SettingId.PlayCountTriggerSeconds, value) AndAlso TypeOf value Is Integer Then
                    playCountTriggerSeconds = DirectCast(value, Integer)
                End If
                value = Nothing
                If mbApiInterface.Setting_GetValue(SettingId.SkipCountTriggerPercent, value) AndAlso TypeOf value Is Integer Then
                    skipCountTriggerPercent = DirectCast(value, Integer) / 100
                End If
                value = Nothing
                If mbApiInterface.Setting_GetValue(SettingId.SkipCountTriggerSeconds, value) AndAlso TypeOf value Is Integer Then
                    skipCountTriggerSeconds = DirectCast(value, Integer)
                End If
                Try
                    startTimeTicks = DateTime.UtcNow.Ticks
                    LogInformation("Initialise", DateTime.Now.ToString())
                    GetNetworkAddresses()
                    server = New MediaServerDevice(Settings.Udn)
                    server.Start()
                    controller = New ControlPointManager
                    controller.Start()
                    AddHandler NetworkChange.NetworkAddressChanged, AddressOf NetworkChange_NetworkAddressChanged
                Catch ex As Exception
                    LogError(ex, "Initialise", ex.StackTrace)
                End Try
            Case NotificationType.FileAddedToLibrary, NotificationType.FileAddedToInbox, NotificationType.FileDeleted, NotificationType.TagsChanged
                ItemManager.SetLibraryDirty()
            Case NotificationType.PlayStateChanged
                If activeRenderingDevice IsNot Nothing Then
                    Select Case mbApiInterface.Player_GetPlayState()
                        Case PlayState.Stopped
                            activeRenderingDevice.StopPlayback()
                        Case PlayState.Paused
                            activeRenderingDevice.PausePlayback()
                        Case PlayState.Playing
                            activeRenderingDevice.ResumePlayback()
                    End Select
                End If
            Case NotificationType.VolumeMuteChanged
                If activeRenderingDevice IsNot Nothing Then
                    activeRenderingDevice.SetMute(mbApiInterface.Player_GetMute())
                End If
            Case NotificationType.VolumeLevelChanged
                If activeRenderingDevice IsNot Nothing Then
                    activeRenderingDevice.SetVolume(mbApiInterface.Player_GetVolume())
                End If
            Case NotificationType.NowPlayingListChanged
                ' F12 - the now-playing-list mutated (track added, removed, reordered, list cleared).
                ' If the plugin already queued a NextURI for gapless transition, that URL may now be
                ' stale. Ask the rendering device to re-evaluate and re-queue if needed.
                If activeRenderingDevice IsNot Nothing Then
                    activeRenderingDevice.RefreshQueuedNextUri()
                End If
        End Select
    End Sub

    Private Shared Sub NetworkChange_NetworkAddressChanged(sender As Object, e As EventArgs)
        Try
            LogInformation("NetworkChange_NetworkAddressChanged", "")
            SyncLock networkLock
                GetNetworkAddresses()
                If server IsNot Nothing Then
                    server.Restart(False)
                End If
                If controller IsNot Nothing Then
                    controller.Restart()
                End If
            End SyncLock
        Catch ex As Exception
            LogError(ex, "NetworkChange_NetworkAddressChanged")
        End Try
    End Sub

    ' One usable IPv4 interface, as discovered by GetNetworkAddresses.
    Private Class NetCandidate
        Public Address As IPAddress
        Public Mask As Byte()
        Public DnsEligible As Boolean
        Public HasGateway As Boolean
        Public Name As String
    End Class

    Private Shared Sub GetNetworkAddresses()
        ' Dns.GetHostAddresses(Dns.GetHostName()).Where(Function(a) a.AddressFamily = AddressFamily.InterNetwork)
        ' Pass 1 - collect every operational IPv4 interface (first IPv4 address each).
        Dim candidates As New List(Of NetCandidate)
        For Each network As NetworkInterface In NetworkInterface.GetAllNetworkInterfaces()
            If network.OperationalStatus = OperationalStatus.Up Then 'AndAlso network.NetworkInterfaceType <> NetworkInterfaceType.Loopback Then
                'LogInformation("GetNetworkAdresseses", "id=" & network.Name & ",speed=" & network.Speed)
                Dim ipProps As IPInterfaceProperties = network.GetIPProperties()
                Dim hasGateway As Boolean = HasIPv4Gateway(ipProps)
                For Each unicastAddress As UnicastIPAddressInformation In ipProps.UnicastAddresses
                    If unicastAddress.Address.AddressFamily = AddressFamily.InterNetwork AndAlso unicastAddress.IPv4Mask IsNot Nothing Then  'IPv4
                        If candidates.FirstOrDefault(Function(c) c.Address.Equals(unicastAddress.Address)) Is Nothing Then
                            candidates.Add(New NetCandidate With {
                                .Address = unicastAddress.Address,
                                .Mask = unicastAddress.IPv4Mask.GetAddressBytes(),
                                .DnsEligible = unicastAddress.IsDnsEligible,
                                .HasGateway = hasGateway,
                                .Name = network.Name
                            })
                        End If
                        Exit For
                    End If
                Next unicastAddress
            End If
        Next network

        ' Pass 2 - decide which interfaces to actually advertise on. A user-pinned IP wins
        ' (announce on that interface only); otherwise "Automatic" keeps only interfaces that
        ' have a default gateway, which drops VPN tunnels (NordLynx) and virtual switches
        ' (Hyper-V / WSL / Docker) that otherwise produce duplicate library cards in control
        ' points. Each selector falls back to "all candidates" so hostAddresses is never empty
        ' (a pinned IP that has gone offline, or a box where no interface reports a gateway).
        Dim pinned As String = Settings.IpAddress
        ipOverrideAddressMatched = String.IsNullOrEmpty(pinned) OrElse
            candidates.Any(Function(c) c.Address.ToString() = pinned)
        Dim selected As List(Of NetCandidate)
        If Not String.IsNullOrEmpty(pinned) AndAlso candidates.Any(Function(c) c.Address.ToString() = pinned) Then
            selected = candidates.Where(Function(c) c.Address.ToString() = pinned).ToList()
        Else
            selected = candidates.Where(Function(c) c.HasGateway).ToList()
            If selected.Count = 0 Then selected = candidates
        End If

        Dim addressList As New List(Of IPAddress)
        Dim subnetMaskList As New List(Of Byte())
        defaultHost = Nothing
        For Each c As NetCandidate In selected
            If c.DnsEligible AndAlso defaultHost Is Nothing Then
                defaultHost = c.Address.ToString()
            End If
            LogInformation("GetNetworkAddresses", c.Address.ToString() & ",dns=" & c.DnsEligible & ",gateway=" & c.HasGateway & ",name=" & c.Name)
            addressList.Add(c.Address)
            subnetMaskList.Add(c.Mask)
        Next c
        hostAddresses = addressList.ToArray()
        subnetMasks = subnetMaskList.ToArray()
        If defaultHost Is Nothing Then
            defaultHost = hostAddresses(0).ToString()
        End If
        'Try
        '    Dim settingsUrl As String = mbApiInterface.Setting_GetPersistentStoragePath() & "UPnPaddress.dat"
        '    If IO.File.Exists(settingsUrl) Then
        '        Using reader As New IO.StreamReader(settingsUrl)
        '            defaultHost = reader.ReadLine()
        '        End Using
        '    End If
        'Catch
        'End Try
        LogInformation("GetNetworkAddresses", PrimaryHostUrl)
        localIpAddresses = New Byte(hostAddresses.Length - 1)() {}
        For index As Integer = 0 To hostAddresses.Length - 1
            localIpAddresses(index) = hostAddresses(index).GetAddressBytes()
        Next index
    End Sub

    ' True when the interface has a real IPv4 default gateway. Used by GetNetworkAddresses to
    ' tell a LAN/Wi-Fi adapter (gateway present) from a VPN tunnel or virtual switch (none) so
    ' "Automatic" only advertises where real control points live.
    Private Shared Function HasIPv4Gateway(ipProps As IPInterfaceProperties) As Boolean
        For Each gw As GatewayIPAddressInformation In ipProps.GatewayAddresses
            If gw.Address IsNot Nothing AndAlso gw.Address.AddressFamily = AddressFamily.InterNetwork _
                    AndAlso Not gw.Address.Equals(IPAddress.Any) Then
                Return True
            End If
        Next gw
        Return False
    End Function

    Private Shared ReadOnly Property PrimaryHostUrl() As String
        Get
            Return "http://" & If(String.IsNullOrEmpty(Settings.IpAddress), defaultHost, Settings.IpAddress) & ":" & boundServerPort
        End Get
    End Property

    ' The Output-To entries MusicBee shows for our discovered renderers are prefixed "UPnP " so it is
    ' clear the UPnP plugin is what surfaced them. MusicBee passes the displayed string back verbatim on
    ' selection, so SetActiveRenderingDevice compares against this same prefixed form (not the raw
    ' device.FriendlyName). Guard against double-prefixing a device that already names itself "UPnP …".
    Private Shared Function RenderingDeviceDisplayName(friendlyName As String) As String
        If String.IsNullOrEmpty(friendlyName) Then Return "UPnP"
        If friendlyName.StartsWith("UPnP ", StringComparison.OrdinalIgnoreCase) Then Return friendlyName
        Return "UPnP " & friendlyName
    End Function

    Public Function GetRenderingDevices() As String()
        Dim list As New List(Of String)
        SyncLock renderingDevices
            For Each device As MediaRendererDevice In renderingDevices
                list.Add(RenderingDeviceDisplayName(device.FriendlyName))
            Next device
        End SyncLock
        Return list.ToArray()
    End Function

    Public Function GetRenderingSettings() As Integer()
        Return New Integer() {CInt(Settings.ContinuousOutput), If(activeRenderingDevice Is Nothing, 44100, activeRenderingDevice.GetContinuousStreamingSampleRate()), 2, If(activeRenderingDevice Is Nothing, 16, activeRenderingDevice.GetContinuousStreamingBitDepth())}
    End Function

    Public Function SetActiveRenderingDevice(name As String) As Boolean
        SyncLock renderingDevices
            If name Is Nothing Then
                If activeRenderingDevice IsNot Nothing Then
                    activeRenderingDevice.Activate(False)
                    activeRenderingDevice = Nothing
                End If
                Return True
            Else
                If activeRenderingDevice IsNot Nothing Then
                    If String.Compare(RenderingDeviceDisplayName(activeRenderingDevice.FriendlyName), name, StringComparison.Ordinal) = 0 Then
                        Return True
                    End If
                    activeRenderingDevice.Activate(False)
                    activeRenderingDevice = Nothing
                End If
                For Each device As MediaRendererDevice In renderingDevices
                    If String.Compare(RenderingDeviceDisplayName(device.FriendlyName), name, StringComparison.Ordinal) = 0 Then
                        activeRenderingDevice = device
                        If activeRenderingDevice.Activate(True) Then
                            Return True
                        Else
                            activeRenderingDevice = Nothing
                            Return False
                        End If
                    End If
                Next device
            End If
        End SyncLock
        Return False
    End Function

    Public Function PlayToDevice(url As String, streamHandle As Integer) As Boolean
        If activeRenderingDevice Is Nothing Then
            LogInformation("PlayToDevice", url & " - no active device")
            Return False
        Else
            Return activeRenderingDevice.PlayToDevice(url, streamHandle)
        End If
    End Function

    Public Function QueueNext(url As String) As Boolean
        If activeRenderingDevice Is Nothing Then
            Return False
        Else
            Return activeRenderingDevice.QueueNext(url)
        End If
    End Function

    Public Function GetPlayPosition() As Integer
        If activeRenderingDevice Is Nothing Then
            Return 0
        Else
            Return activeRenderingDevice.PlayPositionMs
        End If
    End Function

    Public Sub SetPlayPosition(ms As Integer)
        If activeRenderingDevice IsNot Nothing Then
            activeRenderingDevice.Seek(ms)
        End If
    End Sub

    Friend Shared Function LogError(ex As Exception, functionName As String, Optional extra As String = Nothing) As Exception
#If DEBUG Then
        Try
            Dim counter As Integer = Interlocked.Increment(logCounter)
            Dim gap As Long = (DateTime.UtcNow.Ticks - startTimeTicks) \ TimeSpan.TicksPerMillisecond
            SyncLock logLock
                Dim errorMessage As String = gap & "; " & counter & " " & functionName & " - " & ex.Message
                Debug.WriteLine(errorMessage)
                If Not String.IsNullOrEmpty(extra) Then
                    Debug.WriteLine(extra)
                End If
                If Settings.LogDebugInfo Then
                    Dim count As Integer
                    If Not errorCount.TryGetValue(functionName, count) Then
                        errorCount.Add(functionName, 1)
                    ElseIf count = 100 Then
                        Return ex
                    Else
                        errorCount(functionName) = count + 1
                    End If
                    Using writer As New IO.StreamWriter(mbApiInterface.Setting_GetPersistentStoragePath() & "UpnpErrorLog.dat", True)
                        writer.WriteLine(errorMessage)
                        If Not String.IsNullOrEmpty(extra) Then
                            writer.WriteLine(extra)
                        End If
                    End Using
                End If
            End SyncLock
        Catch
        End Try
        Return ex
#Else
        If Settings.LogDebugInfo Then
            Try
                Dim counter As Integer = Interlocked.Increment(logCounter)
                Dim gap As Long = (DateTime.UtcNow.Ticks - startTimeTicks) \ TimeSpan.TicksPerMillisecond
                SyncLock logLock
                    Dim count As Integer
                    If Not errorCount.TryGetValue(functionName, count) Then
                        errorCount.Add(functionName, 1)
                    ElseIf count = 100 Then
                        Return ex
                    Else
                        errorCount(functionName) = count + 1
                    End If
                    Using writer As New IO.StreamWriter(mbApiInterface.Setting_GetPersistentStoragePath() & "UpnpErrorLog.dat", True)
                        writer.WriteLine(gap & "; " & counter & " " & functionName & " - " & ex.Message)
                        If Not String.IsNullOrEmpty(extra) Then
                            writer.WriteLine(extra)
                        End If
                    End Using
                End SyncLock
            Catch
            End Try
        End If
        Return ex
#End If
    End Function

    Private Shared Sub LogInformation(functionName As String, information As String)
#If DEBUG Then
        Try
            Dim counter As Integer = Interlocked.Increment(logCounter)
            Dim gap As Long = (DateTime.UtcNow.Ticks - startTimeTicks) \ TimeSpan.TicksPerMillisecond
            SyncLock logLock
                Dim message As String = gap & "; " & counter & " " & functionName & " - " & information
                Debug.WriteLine(message)
                If Settings.LogDebugInfo Then
                    Using writer As New IO.StreamWriter(mbApiInterface.Setting_GetPersistentStoragePath() & "UpnpErrorLog.dat", True)
                        writer.WriteLine(message)
                    End Using
                End If
            End SyncLock
        Catch
        End Try
#Else
        If Settings.LogDebugInfo Then
            Try
                Dim counter As Integer = Interlocked.Increment(logCounter)
                Dim gap As Long = (DateTime.UtcNow.Ticks - startTimeTicks) \ TimeSpan.TicksPerMillisecond
                SyncLock logLock
                    Using writer As New IO.StreamWriter(mbApiInterface.Setting_GetPersistentStoragePath() & "UpnpErrorLog.dat", True)
                        writer.WriteLine(gap & "; " & counter & " " & functionName & " - " & information)
                    End Using
                End SyncLock
            Catch
            End Try
        End If
#End If
    End Sub

    Friend Enum GainType
        None = 0
        Track = 1
        Album = 2
    End Enum  ' GainType

    ' B2 - per-profile content length policy for problem devices.
    Public Enum ContentLengthMode
        [Default] = 0
        [None] = 1
        PcmOnly = 2
        Fixed = 3
    End Enum

    ' Browse-views per-endpoint model. Each exposed endpoint (filter / playlist / Music tree) has
    ' a list of paths describing how its content is laid out in the UPnP container tree. Paths
    ' come from Templates (saved in UPnPView.ini). Endpoints follow their template by reference
    ' (EndpointBinding.TemplateKey) and carry a stamped copy of its paths for the browse engine;
    ' editing a template re-stamps every endpoint bound to it (View.SyncBindingsFromTemplates).
    Public Enum LeafMode
        AT = 0  ' Album → Tracks (hierarchy fields above the album level, then Album, then Tracks)
        T = 1   ' Flat Tracks (hierarchy fields above the track level, then Tracks directly)
    End Enum

    ' Data-access category of a path / template. Fixed by the kind of node it targets, it
    ' bounds BOTH which fields the path may group by (the picker masks to the category's
    ' field set - a node can only group by fields its data source actually delivers) and
    ' which nodes the template can be applied to. Mirrors the three ways MusicBee surfaces
    ' content (see ItemManager loaders):
    '   Standard - real library files (music, filters, audiobooks, inbox, playlists):
    '              full ID3 via Library_GetFileTags, so every field is available.
    '   Radio    - stream entries (no file): only Folder/Genre carry usable grouping data.
    '   Podcast  - subscription-API tuples: Folder (category), Album (subscription), Year.
    ' Default Standard so old UPnPView.ini templates (no <Category> element) load as Standard.
    Public Enum PathCategory
        Standard = 0
        Radio = 1
        Podcast = 2
    End Enum

    ' One entry in a path's hierarchy. Field is the MetaDataType name to group by; BucketByLetter
    ' inserts an extra A/B/C/... layer ABOVE this field's grouping, useful for large libraries.
    ' Example: Hierarchy = [(Genre, false), (AlbumArtist, true)] renders as
    '   Genre → [Letter] → AlbumArtist → Album → Tracks.
    Public Class HierarchyEntry
        Public Field As String = ""
        Public BucketByLetter As Boolean = False
        ' Sort direction at this hierarchy level. False (default) = Ascending (01,02 / A,B).
        ' True = Descending (02,01 / Z,Y). Cascade-free: each level sorts independently.
        Public SortDescending As Boolean = False
        ' When True, treat values containing "/" as a hierarchy path (e.g. "World/Africa/West")
        ' and build a nested folder tree instead of one flat folder per literal value.
        ' Targets MusicBee's Grouping / Mood / Occasion convention where users encode
        ' hierarchy via "/". Per-entry opt-in because some legitimate values contain "/"
        ' (e.g. "AC/DC", "R&B/Soul") that must NOT be split. Mutually exclusive with
        ' BucketByLetter at the same entry.
        Public TreeBySlash As Boolean = False
        Public Sub New()
        End Sub
        Public Sub New(field As String, bucketByLetter As Boolean)
            Me.Field = field
            Me.BucketByLetter = bucketByLetter
        End Sub
        Public Sub New(field As String, bucketByLetter As Boolean, sortDescending As Boolean)
            Me.Field = field
            Me.BucketByLetter = bucketByLetter
            Me.SortDescending = sortDescending
        End Sub
    End Class

    ' One field in an album's composite group-by key (AT leaf only). The ordered list of these
    ' on a BrowsePath defines, all at once: the album IDENTITY (which tracks collapse into one
    ' album = those sharing every field value), the album TITLE shown (the non-empty field
    ' values joined by " - "), and the album SORT order (album containers ordered by the field
    ' tuple, each field using its own SortDescending). A single grouping with a multi-field
    ' composite key - NOT nested levels (that is the Hierarchy above the leaf). Default {Album}.
    '   {Album}                 → "The Wall"                  (default)
    '   {AlbumArtist, Album}    → "Pink Floyd - The Wall"     (former DisambiguateAlbumByArtist)
    '   {Year↓, Album}          → year desc, album asc        (former SortAlbumsByYearDesc)
    Public Class AlbumGroupField
        Public Field As String = ""
        ' This field's sort direction when ordering album containers. False = ascending.
        Public SortDescending As Boolean = False
        Public Sub New()
        End Sub
        Public Sub New(field As String, sortDescending As Boolean)
            Me.Field = field
            Me.SortDescending = sortDescending
        End Sub
    End Class

    ' One path in a path-set. Hierarchy is the ordered list of grouping entries above the leaf.
    ' Per-path AT-only behaviour:
    '   IncludeAllTracks - each intermediate container gets an extra [All Tracks] entry that
    '                      flattens everything underneath. Meaningless when Leaf=T.
    '   AlbumGroupBy     - ordered field list defining album identity + title + sort at the AT
    '                      leaf (see AlbumGroupField). Meaningless when Leaf=T. Default {Album}.
    Public Class BrowsePath
        <XmlArrayItem("Level")>
        Public Hierarchy() As HierarchyEntry = New HierarchyEntry() {}
        Public Leaf As LeafMode = LeafMode.AT
        Public IncludeAllTracks As Boolean = False
        ' AT-only album grouping. Empty/absent (e.g. old UPnPView.ini) → treated as {Album asc}
        ' by the engine, so pre-existing files keep the classic album-by-title behaviour. Paths
        ' that had the old DisambiguateAlbumByArtist / SortAlbumsByYearDesc booleans lose those
        ' (XmlSerializer drops the unknown elements) and fall back to {Album}.
        <XmlArrayItem("Field")>
        Public AlbumGroupBy() As AlbumGroupField = New AlbumGroupField() {New AlbumGroupField("Album", False)}
        Public Sub New()
        End Sub
    End Class

    ' A named, reusable bundle of paths. Saved in UPnPView.ini. The defaults newly-discovered
    ' endpoints bind to are named by the View.DefaultKey* constants; they are ordinary
    ' templates like any other. No template is "reserved": deletion is gated on use instead
    ' (Delete is disabled while any node follows the template), which automatically protects
    ' whatever each node currently follows - including the Radio/Podcasts category anchors.
    Public Class BrowseTemplate
        ' Stable identity - what EndpointBinding.TemplateKey references. A GUID for every
        ' template: global fixed values for the shipped/seeded ones (identical on every
        ' machine - the shipped UPnPView.ini carries the same values), Guid.NewGuid for
        ' user-created ones. Opaque - only ever compared for equality, never shown in the
        ' UI, never changes; Name is free display text.
        Public Key As String = ""
        Public Name As String = ""
        ' Data-access category (see PathCategory). Constrains the field picker when editing this
        ' template and which endpoints it can be applied to. Default Standard; the reserved
        ' Radio/Podcasts templates are forced to their category on load (View.Load safety net).
        Public Category As PathCategory = PathCategory.Standard
        <XmlArrayItem("Path")>
        Public Paths() As BrowsePath = New BrowsePath() {}
        Public Sub New()
        End Sub
        Public Overrides Function ToString() As String
            Return Name
        End Function
    End Class

    ' Per-endpoint binding: which template the endpoint follows (TemplateKey), the working
    ' copy of its paths the browse engine reads, plus its exposed flag.
    ' EndpointId is a stable identifier: "music" for built-in, "filter:<name>" for filters,
    ' "playlist:<full-path>" for playlists.
    Public Class EndpointBinding
        Public EndpointId As String = ""
        ' Key of the BrowseTemplate this endpoint follows. Template edits re-stamp Paths onto
        ' every binding referencing them (View.SyncBindingsFromTemplates). Empty = unbound:
        ' the endpoint keeps its stamped Paths and follows nothing (the state after the
        ' referenced template is deleted).
        Public TemplateKey As String = ""
        Public Exposed As Boolean = True
        ' When True, this endpoint is also surfaced directly at the UPnP browse root (a "pin"
        ' to top level - it still appears in its natural place). Per-endpoint replacement for
        ' the old global ExposeFiltersAtRoot/ExposePlaylistsAtRoot switches. Default False so
        ' old UPnPView.ini files (no <AtTopLevel> element) load unchanged.
        Public AtTopLevel As Boolean = False
        ' Working copy of the followed template's paths - what the browse engine reads.
        ' PERSISTED ONLY WHEN UNBOUND (see View.Save); for bound bindings it is rebuilt
        ' from the template at load (View.Load → SyncBindingsFromTemplates).
        <XmlArrayItem("Path")>
        Public Paths() As BrowsePath = New BrowsePath() {}
        Public Sub New()
        End Sub
    End Class

    ' Used by the Views tab to enumerate what endpoints exist right now (filters on disk,
    ' playlists from MusicBee, the built-in Music tree). Not persisted - just the runtime
    ' shape of "what's available." Bindings (above) are the per-endpoint persisted config.
    Public Enum EndpointType
        Music = 0       ' built-in Music tree (always one)
        Filter = 1      ' user filter (.xautopf in MusicBee/Filters/)
        Playlist = 2    ' MusicBee playlist (may live inside playlist folders)
        Audiobook = 3   ' built-in Audiobooks tree (always one)
        Inbox = 4       ' built-in Inbox tree (always one)
        NowPlaying = 5  ' the live NowPlaying queue (always one)
        Radio = 6       ' built-in Radio (always one). Items render as audioBroadcast.
        Podcast = 7     ' built-in Podcasts (always one). Folder → Subscription → Episode.
    End Enum

    Public Class EndpointInfo
        Public Id As String
        Public DisplayName As String
        Public Type As EndpointType
        ' For playlists nested in folders, the path components (without the leaf playlist name).
        ' Empty for Music + Filters + top-level Playlists. Used by the TreeView to build the
        ' folder hierarchy that mirrors MusicBee's playlist folder structure.
        Public FolderPath() As String = New String() {}
        Public Sub New(id As String, displayName As String, type As EndpointType)
            Me.Id = id
            Me.DisplayName = displayName
            Me.Type = type
        End Sub
    End Class

    ' Root of UPnPView.ini - the user's browsing setup (curated Fields list + named Templates).
    ' Separate from UPnPSettings.ini so the file is shareable by copy. Plugin extracts an
    ' embedded default resource on first run when this file doesn't exist yet.
    '
    ' Fields = field names the user has opted into for the Group By dropdown (subset of the
    '   non-forced curated set + their Custom/Virtual tags). The 6 forced standard fields
    '   (AlbumArtist, AlbumArtistSort, Composer, ComposerSort, Genre, Year) are always
    '   available and NOT listed here - they're implicit.
    ' Templates = the named path bundles.
    <XmlRoot("UPnPView")>
    Public Class UPnPViewXmlData
        <XmlArrayItem("Template")>
        Public Templates() As BrowseTemplate = New BrowseTemplate() {}
        ' Per-endpoint stamped path-sets. Moved here from UPnPSettings.ini so the user's whole
        ' UPnP browse setup (fields + templates + which endpoint exposes what) lives in one file.
        <XmlArrayItem("Binding")>
        Public EndpointBindings() As EndpointBinding = New EndpointBinding() {}
    End Class

    ' XML-serializable shape of the on-disk settings file (UPnPSettings.ini in MusicBee's AppData).
    ' One per file. Mirrors the Settings static fields + a list of StreamingProfile. Plain public
    ' fields = what XmlSerializer wants. Public class so XmlSerializer can reflect.
    <XmlRoot("UPnPSettings")>
    Public Class UPnPSettingsXmlData
        Public EnablePlayToDevice As Boolean = True
        ' F2.01 - let other apps play TO MusicBee (MusicBee advertises a MediaRenderer device).
        ' Default OFF: opt-in network-control feature; off until the user enables it.
        Public EnableMediaRenderer As Boolean = False
        Public EnableContentAccess As Boolean = True
        Public ServerName As String = "MusicBee Media Library"
        Public RendererName As String = "MusicBee (yaiol)"
        Public IpAddress As String = ""
        Public ServerPort As Integer = 9779
        Public MaxConnections As Integer = 16
        Public Udn As Guid = Guid.Empty
        Public DefaultProfileIndex As Integer = 0
        Public ServerUpdatePlayStatistics As Boolean = True
        Public ContinuousOutput As Boolean = False
        Public BandwidthConstrained As Boolean = False
        ' Always pass through radio streams in their native codec, even when the active
        ' profile would otherwise transcode. Default True - re-encoding a radio stream
        ' is almost always wrong (decodes the source then re-encodes to PCM/L16 which
        ' multiplies bandwidth ~20× and breaks playback on some renderers like BubbleUPnP).
        Public ForceNativeStreamForRadio As Boolean = True
        Public LogDebugInfo As Boolean = False
        ' If True, truncates UpnpErrorLog.dat to zero bytes at every plugin startup.
        ' Useful during development to keep the log focused on the current session.
        Public ClearLogOnStartup As Boolean = False
        ' WYSIWYG prefixes prepended to a filter/playlist name when it is pinned to the UPnP
        ' browse root (per-endpoint EndpointBinding.AtTopLevel) - purely literal, no auto-space.
        Public FilterPrefix As String = "Filter: "
        Public PlaylistPrefix As String = "Playlist: "
        ' Source a client's random request draws from. Empty = the whole music library
        ' (default); otherwise the basename of a MusicBee filter (.xautopf), whose
        ' conditions scope the pick. See Settings.RandomSourceFilter.
        Public RandomSourceFilter As String = ""
        ' Hierarchical fields - newline-joined "field<TAB>char" rows. Each names a field whose
        ' tag values are split on the given 1-char delimiter into a browse sub-tree (e.g.
        ' Grouping + "/" → Jazz/Cool Jazz renders as Jazz › Cool Jazz). Empty = none.
        Public HierarchicalFields As String = ""
        <XmlArrayItem("Profile")>
        Public StreamingProfiles() As StreamingProfile = New StreamingProfile() {}
    End Class

    ' Public because XmlSerializer needs to reach the type for serialization. The class stays
    ' nested in Plugin so callers still write `Plugin.StreamingProfile`.
    Public Class StreamingProfile
        Public ProfileName As String
        Public UserAgents() As String = New String() {}
        Public PictureSize As UShort = 160
        Public MinimumSampleRate As Integer = 44100
        Public MaximumSampleRate As Integer = 48000
        Public StereoOnly As Boolean = True
        Public MaximumBitDepth As Integer = 16
        Public TranscodeCodec As FileCodec = FileCodec.Pcm
        Public TranscodeQuality As EncodeQuality = EncodeQuality.HighQuality
        Public TranscodeBitDepth As Integer = 16
        Public TranscodeSampleRate As Integer = -1
        Public WmcCompatability As Boolean = False
        ' B2 - problem device settings (per profile).
        Public ForceLittleEndianPcm As Boolean = False
        Public DoNotUseRawPcm As Boolean = False
        Public ContentLength As ContentLengthMode = ContentLengthMode.[Default]
        ' F3 - bypass transcoding for this device; send the original file bytes. Default ON, matching 2025 fork.
        Public ForceNativeStream As Boolean = True
        ' Per-profile audio-processing flags (formerly global Settings.ServerEnableSoundEffects / ServerReplayGainMode).
        ' Both default OFF so we don't silently transcode files the user wasn't expecting to be processed.
        Public EnableSoundEffects As Boolean = False
        Public EnableReplayGain As Boolean = False
        ' B5 - gapless / NextURI per-profile options.
        ' F11: gapless is opt-in per device - default ON for devices that advertise SetNextAVTransportURI,
        '      user unticks for devices with buggy NextURI implementations. Was originally global+override
        '      (Settings.EnablePlayToSetNext + per-profile DisableNextUri) until v10 schema flipped it to
        '      purely per-profile, symmetric with F4 ForceTranscoding.
        Public EnableNextUri As Boolean = True
        ' F8: some devices (notably Denon) stop immediately when sent a blank NextURI, so prevent the
        '     plugin from ever clearing it. Default OFF; user opts in per profile.
        Public DoNotClearNextUri As Boolean = False
        ' F4 - force every stream through the transcoder for THIS device. Moved from global Settings
        ' (where it was contradictory with the per-profile ForceNativeStream) to per-profile. Mutually
        ' exclusive with ForceNativeStream - UI auto-unticks the other when either is toggled on.
        ' When both somehow end up True at runtime, ForceTranscoding wins (see WriteAudioFileDIDL).
        Public ForceTranscoding As Boolean = False
        Public Sub New()
        End Sub
        Public Sub New(name As String)
            ProfileName = name
        End Sub
        Public Overrides Function ToString() As String
            Return ProfileName
        End Function
    End Class  ' StreamingProfile

    Friend Class Settings
        Public Shared EnableContentAccess As Boolean = True
        Public Shared EnablePlayToDevice As Boolean = True
        Public Shared EnableMediaRenderer As Boolean = False
        Public Shared ServerName As String = "MusicBee Media Library"
        Public Shared RendererName As String = "MusicBee (yaiol)"
        Public Shared IpAddress As String = ""
        Public Shared ServerPort As Integer = 9779
        ' F45 - max concurrent stream slots. Default 16; field exposed in the General settings page.
        ' Changing it requires a MusicBee restart because the SemaphoreSlim is initialized at type load.
        Public Shared MaxConnections As Integer = 16
        Public Shared Udn As Guid = Guid.Empty
        Public Shared DefaultProfileIndex As Integer = 0
        Public Shared StreamingProfiles As New List(Of StreamingProfile)
        Public Shared ProfileTemplates As New List(Of StreamingProfile)
        Public Shared ServerUpdatePlayStatistics As Boolean = True
        Public Shared ForceNativeStreamForRadio As Boolean = True
        ' Legacy hardcoded Music tree's auto-bucketing thresholds. Constants now - the global
        ' user-facing toggle was retired in favour of per-path [Letter] bucket checkboxes.
        ' These constants only affect the still-hardcoded Music sub-tree; they go away when
        ' Music converts to use bindings.
        Public Const BucketNodes As Boolean = True
        Public Const BucketTrigger As Integer = 500
        Public Shared ContinuousOutput As Boolean = False
        ' EnablePlayToSetNext was a global master toggle for NextURI gapless (v7-v9). Removed in v10
        ' when gapless became purely per-profile (StreamingProfile.EnableNextUri). Schema migration
        ' in Shared Sub New() copies the legacy global value into all profiles when loading v7-v9.
        Public Shared AllowInternetConnections As Boolean = False
        Public Shared InternetHostAddress As String = ""
        Public Shared InternetUsername As String = ""
        Public Shared InternetPassword As String = ""
        Public Shared TryPortForwarding As Boolean = False
        Public Shared BandwidthConstrained As Boolean = False
        Public Shared LogDebugInfo As Boolean = False
        Public Shared ClearLogOnStartup As Boolean = False
        ' F4 - was a single global toggle before v9; moved to per-profile (StreamingProfile.ForceTranscoding)
        ' for symmetry with ForceNativeStream. Schema migration in Shared Sub New() copies the legacy
        ' global value into all profiles when loading a v < 9 file.
        ' WYSIWYG prefixes for filter/playlist names when pinned to the browse root (see DTO).
        Public Shared FilterPrefix As String = "Filter: "
        Public Shared PlaylistPrefix As String = "Playlist: "
        ' Scope of the random pick a control point asks for. A client's "Random Tracks" /
        ' "Random Albums" is a bare class-only Search with no container, so without this it
        ' always drew from the whole library. Empty = whole music library (default);
        ' otherwise a filter basename, applied as "filter:<name>" in ItemManager's
        ' RandomSourceEndpoint. An EXPLICIT container scope from the client still wins.
        Public Shared RandomSourceFilter As String = ""
        ' Field name → 1-char delimiter. Built from the DTO string on load; any field listed
        ' here renders as a slash-tree at every group-by level that uses it (see ItemManager).
        Public Shared HierarchicalFields As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ' View-side state (EndpointBindings, BrowseTemplates, EnabledGroupingFields, helpers) is
        ' now in the View class (parallel to Settings). All callsites use View.X.
        Private Shared ReadOnly userAgentProfiles As New Dictionary(Of String, StreamingProfile)(StringComparer.OrdinalIgnoreCase)

        Shared Sub New()
            ' UPnPSettings.ini lives in MusicBee's AppData alongside other settings files. Format:
            ' XML serialized via System.Xml.Serialization (matches MusicBee's own conventions).
            ' Schema is implicit (the UPnPSettingsXmlData class shape). Adding a field = add a
            ' public field to UPnPSettingsXmlData + copy it in/out below. Removing a field = delete
            ' it from UPnPSettingsXmlData + remove the copy lines. Old files with extra/missing
            ' fields load cleanly: extras ignored, missing defaults from the class init.
            ' See sibling CLAUDE.md for the full rationale on why this is much simpler than the
            ' BinaryReader/Writer mess that lived here before.
            Dim settingsUrl As String = mbApiInterface.Setting_GetPersistentStoragePath() & "UPnPSettings.ini"
            Dim profile As StreamingProfile
            Dim data As New UPnPSettingsXmlData()
            If IO.File.Exists(settingsUrl) Then
                Try
                    Using stream As New IO.FileStream(settingsUrl, IO.FileMode.Open, IO.FileAccess.Read)
                        Dim serializer As New XmlSerializer(GetType(UPnPSettingsXmlData))
                        data = DirectCast(serializer.Deserialize(stream), UPnPSettingsXmlData)
                    End Using
                Catch ex As Exception
                    LogError(ex, "Settings.Load", settingsUrl)
                End Try
            End If
            EnablePlayToDevice = data.EnablePlayToDevice
            EnableMediaRenderer = data.EnableMediaRenderer
            EnableContentAccess = data.EnableContentAccess
            ServerName = data.ServerName
            RendererName = data.RendererName
            ServerPort = data.ServerPort
            MaxConnections = data.MaxConnections
            Udn = data.Udn
            IpAddress = data.IpAddress
            DefaultProfileIndex = data.DefaultProfileIndex
            ServerUpdatePlayStatistics = data.ServerUpdatePlayStatistics
            ContinuousOutput = data.ContinuousOutput
            BandwidthConstrained = data.BandwidthConstrained
            ForceNativeStreamForRadio = data.ForceNativeStreamForRadio
            LogDebugInfo = data.LogDebugInfo
            ClearLogOnStartup = data.ClearLogOnStartup
            FilterPrefix = If(data.FilterPrefix, "")
            PlaylistPrefix = If(data.PlaylistPrefix, "")
            RandomSourceFilter = If(data.RandomSourceFilter, "")
            HierarchicalFields = ParseHierarchicalFields(data.HierarchicalFields)
            If data.StreamingProfiles IsNot Nothing Then
                For Each p As StreamingProfile In data.StreamingProfiles
                    StreamingProfiles.Add(p)
                Next
            End If
            ' EndpointBindings + BrowseTemplates + EnabledGroupingFields are loaded by the View
            ' class's static constructor (triggered on first access). No load call here.
            If Udn = Guid.Empty Then
                Udn = Guid.NewGuid()
            End If
            profile = New StreamingProfile("New Profile")
            ProfileTemplates.Add(profile)
            profile = New StreamingProfile("BubbleUPnP") With {
                .UserAgents = New String() {"BubbleUPnP"},
                .MinimumSampleRate = 11025,
                .MaximumSampleRate = 48000,
                .MaximumBitDepth = 16
            }
            ProfileTemplates.Add(profile)
            profile = New StreamingProfile("foobar2000") With {
                .UserAgents = New String() {"foobar2000"},
                .MinimumSampleRate = 11025,
                .MaximumSampleRate = 2822400,
                .MaximumBitDepth = 24,
                .StereoOnly = False
            }
            ProfileTemplates.Add(profile)
            profile = New StreamingProfile("JRiver Media Center") With {
                .UserAgents = New String() {"JRiver", "J. River"},
                .MinimumSampleRate = 11025,
                .MaximumSampleRate = 192000,
                .MaximumBitDepth = 24,
                .StereoOnly = False
            }
            ProfileTemplates.Add(profile)
            profile = New StreamingProfile("Linn DS") With {
                .UserAgents = New String() {"Linn", "ChorusDS", "BubbleDS"},
                .MinimumSampleRate = 11025,
                .MaximumSampleRate = 192000,
                .MaximumBitDepth = 24
            }
            ProfileTemplates.Add(profile)
            profile = New StreamingProfile("Playstation 3") With {
                .UserAgents = New String() {"PLAYSTATION 3"},
                .MinimumSampleRate = 44100,
                .MaximumSampleRate = 176400,
                .MaximumBitDepth = 16
            }
            ProfileTemplates.Add(profile)
            profile = New StreamingProfile("PlugPlayer") With {
                .UserAgents = New String() {"PlugPlayer (iOS)"},
                .MinimumSampleRate = 11025,
                .MaximumSampleRate = 48000,
                .MaximumBitDepth = 16
            }
            ProfileTemplates.Add(profile)
            profile = New StreamingProfile("UPnPlay") With {
                .UserAgents = New String() {"UPnPlay"},
                .MinimumSampleRate = 11025,
                .MaximumSampleRate = 48000,
                .MaximumBitDepth = 16
            }
            ProfileTemplates.Add(profile)
            profile = New StreamingProfile("Windows Media Player") With {
                .UserAgents = New String() {"Windows-Media-Player", "WMFSDK", "Windows Media Player"},
                .MinimumSampleRate = 11025,
                .MaximumSampleRate = 192000,
                .MaximumBitDepth = 24,
                .StereoOnly = False,
                .WmcCompatability = True
            }
            ProfileTemplates.Add(profile)
            profile = New StreamingProfile("Xbox 360") With {
                .UserAgents = New String() {"Xenon", "Xbox"},
                .MinimumSampleRate = 44100,
                .MaximumSampleRate = 48000,
                .MaximumBitDepth = 16,
                .WmcCompatability = True
            }
            ProfileTemplates.Add(profile)
            ' F1 - Profiles added by the 2025 fork.
            profile = New StreamingProfile("Playstation 4") With {
                .UserAgents = New String() {"PLAYSTATION 4"},
                .MinimumSampleRate = 44100,
                .MaximumSampleRate = 192000,
                .MaximumBitDepth = 24
            }
            ProfileTemplates.Add(profile)
            profile = New StreamingProfile("Xbox 360/One") With {
                .UserAgents = New String() {"Xenon", "Xbox", "XBOXONE"},
                .MinimumSampleRate = 44100,
                .MaximumSampleRate = 48000,
                .MaximumBitDepth = 16,
                .WmcCompatability = True
            }
            ProfileTemplates.Add(profile)
            If StreamingProfiles.Count = 0 Then
                StreamingProfiles.Add(New StreamingProfile("Generic Device"))
                For index As Integer = 1 To ProfileTemplates.Count - 1
                    StreamingProfiles.Add(ProfileTemplates(index))
                Next index
            End If
        End Sub

        Public Shared Sub SaveSettings()
            Dim settingsUrl As String = mbApiInterface.Setting_GetPersistentStoragePath() & "UPnPSettings.ini"
            Try
                Dim data As New UPnPSettingsXmlData() With {
                    .EnablePlayToDevice = EnablePlayToDevice,
                    .EnableMediaRenderer = EnableMediaRenderer,
                    .EnableContentAccess = EnableContentAccess,
                    .ServerName = ServerName,
                    .RendererName = RendererName,
                    .IpAddress = IpAddress,
                    .ServerPort = ServerPort,
                    .MaxConnections = MaxConnections,
                    .Udn = Udn,
                    .DefaultProfileIndex = DefaultProfileIndex,
                    .ServerUpdatePlayStatistics = ServerUpdatePlayStatistics,
                    .ContinuousOutput = ContinuousOutput,
                    .BandwidthConstrained = BandwidthConstrained,
                    .ForceNativeStreamForRadio = ForceNativeStreamForRadio,
                    .LogDebugInfo = LogDebugInfo,
                    .ClearLogOnStartup = ClearLogOnStartup,
                    .FilterPrefix = FilterPrefix,
                    .PlaylistPrefix = PlaylistPrefix,
                    .RandomSourceFilter = RandomSourceFilter,
                    .HierarchicalFields = SerializeHierarchicalFields(HierarchicalFields),
                    .StreamingProfiles = StreamingProfiles.ToArray()
                }
                Using stream As New IO.FileStream(settingsUrl, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.None)
                    Dim serializer As New XmlSerializer(GetType(UPnPSettingsXmlData))
                    serializer.Serialize(stream, data)
                End Using
            Catch ex As Exception
                LogError(ex, "Settings.Save", settingsUrl)
            End Try
            View.Save()
        End Sub

        ' The 1-char hierarchical delimiter configured for a field, or "" if the field is flat.
        Public Shared Function HierarchicalCharFor(fieldName As String) As String
            If String.IsNullOrEmpty(fieldName) Then Return ""
            Dim c As String = Nothing
            If HierarchicalFields.TryGetValue(fieldName, c) AndAlso Not String.IsNullOrEmpty(c) Then Return c.Substring(0, 1)
            Return ""
        End Function

        ' "field<TAB>char" rows joined by LF ↔ a field→char dictionary. Rows missing either
        ' part are dropped; the char is truncated to a single character.
        Private Shared Function ParseHierarchicalFields(raw As String) As Dictionary(Of String, String)
            Dim d As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            If String.IsNullOrEmpty(raw) Then Return d
            For Each line As String In raw.Split(ControlChars.Lf)
                Dim parts() As String = line.Split(ControlChars.Tab)
                If parts.Length = 2 AndAlso parts(0).Length > 0 AndAlso parts(1).Length > 0 Then
                    d(parts(0)) = parts(1).Substring(0, 1)
                End If
            Next
            Return d
        End Function

        Private Shared Function SerializeHierarchicalFields(d As Dictionary(Of String, String)) As String
            If d Is Nothing OrElse d.Count = 0 Then Return ""
            Dim sb As New System.Text.StringBuilder()
            For Each kv As KeyValuePair(Of String, String) In d
                If String.IsNullOrEmpty(kv.Key) OrElse String.IsNullOrEmpty(kv.Value) Then Continue For
                If sb.Length > 0 Then sb.Append(ControlChars.Lf)
                sb.Append(kv.Key).Append(ControlChars.Tab).Append(kv.Value.Substring(0, 1))
            Next
            Return sb.ToString()
        End Function

        Public Shared Function GetStreamingProfile(requestHeaders As Dictionary(Of String, String)) As StreamingProfile
            Dim userAgent As String
            If requestHeaders.TryGetValue("X-AV-Client-Info", userAgent) AndAlso userAgent.IndexOf("PLAYSTATION 3", StringComparison.OrdinalIgnoreCase) <> -1 Then
                userAgent = "PLAYSTATION 3"
            ElseIf Not requestHeaders.TryGetValue("User-Agent", userAgent) Then
                userAgent = ""
            End If
            SyncLock userAgentProfiles
                Dim profile As StreamingProfile = Nothing
                If Not userAgentProfiles.TryGetValue(userAgent, profile) Then
                    For index As Integer = 1 To StreamingProfiles.Count - 1
                        profile = StreamingProfiles(index)
                        For agentIndex As Integer = 0 To profile.UserAgents.Length - 1
                            If userAgent.IndexOf(profile.UserAgents(agentIndex), StringComparison.OrdinalIgnoreCase) <> -1 Then
                                userAgentProfiles.Add(userAgent, profile)
                                LogInformation("Profile", profile.ProfileName & ", useragent=" & userAgent)
                                Return profile
                            End If
                        Next agentIndex
                    Next index
                    profile = StreamingProfiles(0)
                    userAgentProfiles.Add(userAgent, profile)
                End If
                LogInformation("Profile", profile.ProfileName & ", useragent=" & userAgent)
                Return profile
            End SyncLock
        End Function
    End Class  ' Settings

    ' View - everything the user's UPnP browse-view setup defines:
    '   • EnabledGroupingFields - user-curated Group By options beyond the forced 6.
    '   • BrowseTemplates       - named, reusable view path bundles.
    '   • EndpointBindings      - per-endpoint stamped paths (the output of applying a template).
    '   • Helpers               - field-name resolution, label formatting, endpoint discovery.
    ' Persisted in %AppData%\MusicBee\UPnPView.ini (separate from UPnPSettings.ini so the user
    ' can share their setup by copying just this file). All members are static-like.
    Friend Class View
        ' Forced field names = canonical Plugin.MetaDataType enum names. Always available in
        ' the UPnP View Group By dropdown; the Fields tab shows them in a locked sub-group.
        ' Never stored in EnabledGroupingFields - they're implicit.
        Public Shared ReadOnly ForcedStandardFields() As String = New String() {
            "AlbumArtist", "SortAlbumArtist", "Composer", "SortComposer", "Genre", "Year"
        }
        ' Synthetic / Extra field names - NOT backed by MusicBee's MetaDataType enum. Each
        ' maps to a dedicated slot in the tags() array (see MetaDataIndex.ExtraField1, …)
        ' that loaders populate explicitly (e.g. LoadPodcastFiles writes the subscription
        ' folder into ExtraField1 when the user adds "Folder" to a grouping path).
        ' Listed here so the Fields tab and the field dropdown can offer them alongside
        ' MB-native fields. Kept separate from Custom/Virtual so users don't lose a real
        ' Custom slot to a synthetic concept.
        ' Token format mirrors MB's MetaDataType naming: no spaces (so it round-trips
        ' cleanly through the XML persistence layer alongside "AlbumArtist", "Genre",
        ' etc.). The user-visible label "MusicBee Folder" is produced by FieldDisplayName.
        Public Shared ReadOnly SyntheticFields() As String = New String() {
            "MusicBeeFolder"
        }

        ' =================================================================================
        ' Field categorization for the Group By picker.
        '
        ' Single source of truth for which fields appear in the picker and where they appear.
        ' To move a field between categories: delete its name from one array, add it to
        ' another, rebuild. No persistence / no schema / no migration.
        '
        ' Top-level groups (in display order):
        '   1. PickerCommonFields    - flat, at the top of the picker. The fields users
        '                              actually reach for. 10 items.
        '   2. PickerPeopleFields    - "People" submenu: artist / contributor variants.
        '   3. PickerMoodFields      - "Mood & Context" submenu.
        '   4. PickerAlbumWorkFields - "Album & Work" submenu.
        '   5. PickerRatingFields    - "Ratings & Status" submenu.
        '   6. PickerTechnicalFields - "Technical" submenu.
        '   7. Custom Tags submenu   - dynamically from Custom1..16, only slots where MB
        '                              returns a user-set name. Built at picker-open time,
        '                              not listed here.
        '   8. Virtual Tags submenu  - same dynamic rule for Virtual1..25.
        '
        ' PickerHiddenFields below = never shown anywhere. Per-track identifiers (one bucket
        ' per track = useless as grouping), binary / long-text fields (garbage as group key),
        ' and twin-display-label twins (AlbumArtistRaw vs AlbumArtist).
        '
        ' "MusicBeeFolder" sits in PickerCommonFields even though it's a synthetic field
        ' (not in MetaDataType) - it's a yaiol-specific concept the user grouping by Folder
        ' uses heavily, so it earns a top-level slot. The synthetic Extra group is gone.
        '
        ' ForcedStandardFields above is preserved for back-compat with the EnabledGrouping-
        ' Fields persistence (still loaded from UPnPView.ini until Pass 3 removes it) but
        ' is NOT consulted by the picker - categorization here is the only authority.
        Public Shared ReadOnly PickerCommonFields() As String = New String() {
            "Album", "AlbumArtist", "SortAlbumArtist", "Artist",
            "Composer", "SortComposer", "Genre", "GenreCategory",
            "Year", "YearOnly", "MusicBeeFolder"
        }
        ' MultiArtist (33), MultiComposer (89), PrimaryArtist (19) were dropped: their
        ' MetaDataType slot IDs have been repurposed in newer MB builds, so
        ' Setting_GetFieldName returns garbage labels ("ComposerPeople" /
        ' "FileDuplicateFlag" / etc.) for them. The plural "Artists" (slot 144) is MB's
        ' modern canonical multi-value artist field and covers the same use case.
        Public Shared ReadOnly PickerPeopleFields() As String = New String() {
            "Artists", "ArtistsWithArtistRole", "ArtistsWithGuestRole",
            "ArtistsWithPerformerRole", "ArtistsWithRemixerRole",
            "SortArtist", "OriginalArtist", "Conductor", "Lyricist"
        }
        Public Shared ReadOnly PickerMoodFields() As String = New String() {
            "Mood", "Occasion", "Origin", "Genres",
            "Keywords", "Grouping", "Language"
        }
        Public Shared ReadOnly PickerAlbumWorkFields() As String = New String() {
            "SortAlbum", "Work", "Publisher", "RatingAlbum", "OriginalYear"
        }
        Public Shared ReadOnly PickerRatingFields() As String = New String() {
            "Rating", "RatingLove", "HasLyrics", "ShowMovement"
        }
        Public Shared ReadOnly PickerTechnicalFields() As String = New String() {
            "BeatsPerMin", "Tempo", "Quality", "Encoder"
        }
        Public Shared ReadOnly PickerHiddenFields() As String = New String() {
            "TrackTitle", "TrackNo", "DiscNo",
            "MovementName", "MovementNo", "MovementCount",
            "DiscCount", "TrackCount", "OriginalTitle",
            "Artwork", "Lyrics", "Comment",
            "AlbumArtistRaw", "SortTitle"
        }

        ' ---- Path categories (data-access model) ----------------------------------------
        ' Per-category groupable-field allow-lists (see PathCategory). Standard = everything,
        ' so it has no list and FieldAllowedInCategory returns True for it. Radio/Podcast list
        ' only the fields their data source populates AND that make sense to group by; the
        ' picker masks to these so a path can't reference a field the node can't deliver.
        ' Title / Duration / Bitrate are deliberately omitted - they're per-item unique, so
        ' grouping by them produces one-entry folders. Tokens must be real picker tokens that
        ' RESOLVE on the endpoint's path. Podcasts are an eager endpoint, where the year token
        ' must be "Year" (resolves to the Year tag slot the podcast loader fills from ep[2]) -
        ' "YearOnly" does not resolve on the eager path. "MusicBeeFolder" = the folder field.
        Public Shared ReadOnly RadioCategoryFields() As String = New String() {
            "MusicBeeFolder", "Genre"
        }
        Public Shared ReadOnly PodcastCategoryFields() As String = New String() {
            "MusicBeeFolder", "Album", "Year"
        }

        ' The PathCategory a given endpoint type belongs to. Radio and Podcast are their own
        ' categories; everything else (Music, Filter, Playlist, Audiobook, Inbox, NowPlaying)
        ' reads real library files → Standard.
        Public Shared Function CategoryForEndpointType(t As EndpointType) As PathCategory
            Select Case t
                Case EndpointType.Radio
                    Return PathCategory.Radio
                Case EndpointType.Podcast
                    Return PathCategory.Podcast
                Case Else
                    Return PathCategory.Standard
            End Select
        End Function

        ' True if a field token may be used in a path of the given category. Standard allows
        ' every field; Radio/Podcast restrict to their data-backed grouping field lists.
        Public Shared Function FieldAllowedInCategory(cat As PathCategory, fieldName As String) As Boolean
            Select Case cat
                Case PathCategory.Radio
                    Return RadioCategoryFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase)
                Case PathCategory.Podcast
                    Return PodcastCategoryFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase)
                Case Else
                    Return True
            End Select
        End Function

        ' Default template keys - global fixed GUIDs, identical on every machine (the shipped
        ' UPnPView.ini carries the same values). These are what newly-discovered endpoints
        ' get bound to (EnsureEndpointBindings). They are ordinary templates like any other:
        ' no template is "reserved" - deletion is simply gated on use (the dialog disables
        ' Delete while any node follows the template), which automatically protects whatever
        ' each node currently follows. If a default was deleted (possible only while unused),
        ' seeding falls back to the first template of the node's category.
        Public Const DefaultKeyByAlbumArtist As String = "53ff60cde88d44f3b28df4e5cc9dedbd"
        Public Const DefaultKeyTracks As String = "fdfdea1912a64acf8ed4fbb2505956cc"
        Public Const DefaultKeyMusic As String = "94075c21b4184aeda59f8b69cff334c3"
        Public Const DefaultKeyRadio As String = "22025167bdfe45e4b751b2d6ac786b1a"
        Public Const DefaultKeyPodcast As String = "f2446a5ddc584b2dbc442173e575f58a"
        ' Display names - used for the key backfill of pre-Key files and (Radio/Podcasts,
        ' which are code-seeded rather than shipped in the file) for the auto-seed.
        Public Const DefaultNameByAlbumArtist As String = "by Album Artist"
        Public Const DefaultNameTracks As String = "Tracks"
        Public Const DefaultNameMusic As String = "Music"
        Public Const DefaultNameRadio As String = "Radio"
        Public Const DefaultNamePodcast As String = "Podcasts"

        ' Per-endpoint stamped path-sets - the runtime state of "what each endpoint exposes".
        Public Shared EndpointBindings As New List(Of EndpointBinding)
        ' Named templates - the user's reusable view path bundles.
        Public Shared BrowseTemplates As New List(Of BrowseTemplate)

        Shared Sub New()
            Load()
        End Sub

        ' Load Templates + EndpointBindings from UPnPView.ini. On first run (file missing)
        ' extract the embedded default. The legacy `Fields` element (user-curated list of
        ' Group By options) is no longer read - categorization in PickerCommonFields /
        ' PickerPeopleFields / etc. is the single source of truth for the picker. Old XML
        ' files still load cleanly (XmlSerializer silently ignores the orphaned <Fields>
        ' element); the element is no longer written on save either, so it disappears on
        ' the next save.
        ' Friend (not Private) so the settings dialog can re-read on Cancel to discard the
        ' in-memory edits it makes live (and re-seed any reserved template the user deleted).
        Friend Shared Sub Load()
            BrowseTemplates.Clear()
            EndpointBindings.Clear()
            Dim viewUrl As String = mbApiInterface.Setting_GetPersistentStoragePath() & "UPnPView.ini"
            Dim firstRun As Boolean = Not IO.File.Exists(viewUrl)
            If firstRun Then
                Try
                    Dim asm As Reflection.Assembly = Reflection.Assembly.GetExecutingAssembly()
                    Using rs As IO.Stream = asm.GetManifestResourceStream("MusicBeePlugin.UPnPView.ini")
                        If rs IsNot Nothing Then
                            Using fs As New IO.FileStream(viewUrl, IO.FileMode.Create, IO.FileAccess.Write)
                                rs.CopyTo(fs)
                            End Using
                        End If
                    End Using
                Catch ex As Exception
                    LogError(ex, "View.FirstRunExtract", viewUrl)
                End Try
            End If
            If IO.File.Exists(viewUrl) Then
                Try
                    Using stream As New IO.FileStream(viewUrl, IO.FileMode.Open, IO.FileAccess.Read)
                        Dim serializer As New XmlSerializer(GetType(UPnPViewXmlData))
                        Dim data As UPnPViewXmlData = DirectCast(serializer.Deserialize(stream), UPnPViewXmlData)
                        If data.Templates IsNot Nothing Then
                            For Each t As BrowseTemplate In data.Templates
                                BrowseTemplates.Add(t)
                            Next
                        End If
                        If data.EndpointBindings IsNot Nothing Then
                            For Each b As EndpointBinding In data.EndpointBindings
                                EndpointBindings.Add(b)
                            Next
                        End If
                    End Using
                Catch ex As Exception
                    LogError(ex, "View.Load", viewUrl)
                End Try
            End If
            ' Key backfill: templates without a Key (file saved before the Key field existed,
            ' or a template hand-added to the XML) get their identity here - reserved ones by
            ' display-name match, user ones a fresh GUID (stable once the file is next saved).
            For Each t As BrowseTemplate In BrowseTemplates
                If String.IsNullOrEmpty(t.Key) Then
                    Select Case t.Name
                        Case DefaultNameByAlbumArtist : t.Key = DefaultKeyByAlbumArtist
                        Case DefaultNameTracks : t.Key = DefaultKeyTracks
                        Case DefaultNameMusic : t.Key = DefaultKeyMusic
                        Case DefaultNameRadio : t.Key = DefaultKeyRadio
                        Case DefaultNamePodcast : t.Key = DefaultKeyPodcast
                        Case Else : t.Key = Guid.NewGuid().ToString("N")
                    End Select
                End If
            Next
            ' The Standard defaults ship in the embedded UPnPView.ini and are NOT re-seeded
            ' here - deleting one (possible only while no node follows it) is a user choice
            ' that sticks. Radio and Podcasts are the two code-seeded templates: each starts
            ' its category (the category band exists only through its templates), so a fresh
            ' or pre-category file gets them here.
            ' Radio - group by MusicBee Folder (the "folder" attribute on the radio
            ' station edit dialog, populated into ExtraField1 by LoadRadioFiles). Flat
            ' track leaf since radio entries lack album/disc/track metadata.
            If Not BrowseTemplates.Any(Function(t) t.Key = DefaultKeyRadio) Then
                BrowseTemplates.Add(New BrowseTemplate With {
                    .Key = DefaultKeyRadio,
                    .Name = DefaultNameRadio,
                    .Paths = New BrowsePath() {
                        New BrowsePath With {
                            .Hierarchy = New HierarchyEntry() {New HierarchyEntry("MusicBeeFolder", False)},
                            .Leaf = LeafMode.T
                        }
                    }
                })
            End If
            ' Podcasts - Folder → Subscription → Episodes. "Folder" is a synthetic
            ' field populated from MusicBee's per-subscription folder attribute (see
            ' LoadPodcastFiles, MetaDataIndex.ExtraField1). "Album" holds the subscription
            ' name. The flat-track leaf yields the episodes under each subscription.
            If Not BrowseTemplates.Any(Function(t) t.Key = DefaultKeyPodcast) Then
                BrowseTemplates.Add(New BrowseTemplate With {
                    .Key = DefaultKeyPodcast,
                    .Name = DefaultNamePodcast,
                    .Paths = New BrowsePath() {
                        New BrowsePath With {
                            .Hierarchy = New HierarchyEntry() {
                                New HierarchyEntry("MusicBeeFolder", False),
                                New HierarchyEntry("Album", False)
                            },
                            .Leaf = LeafMode.T
                        }
                    }
                })
            End If
            ' The Radio/Podcasts templates are category-typed. Force their category on every
            ' load so pre-category UPnPView.ini files (whose <Template> has no <Category>,
            ' deserializing to the Standard default) get corrected. Idempotent - same result
            ' whether the template was just seeded above or loaded from disk.
            For Each t As BrowseTemplate In BrowseTemplates
                If t.Key = DefaultKeyRadio Then
                    t.Category = PathCategory.Radio
                ElseIf t.Key = DefaultKeyPodcast Then
                    t.Category = PathCategory.Podcast
                End If
            Next
            ' Bound bindings persist only their TemplateKey (see Save) - stamp their working
            ' Paths from the templates now that those are loaded and seeded. Also flattens any
            ' stale Paths copy a pre-normalization file still carries for a bound binding.
            SyncBindingsFromTemplates()
        End Sub

        ' Persist current state back to UPnPView.ini. Called from Settings.SaveSettings.
        Public Shared Sub Save()
            Dim viewUrl As String = mbApiInterface.Setting_GetPersistentStoragePath() & "UPnPView.ini"
            Try
                ' Normalized persistence: a BOUND binding writes only its TemplateKey - the
                ' template is the truth and Load re-stamps the working copy, so writing Paths
                ' would just duplicate the template into every follower. Only unbound bindings
                ' (they follow nothing) persist their Paths. The copies below are throwaway
                ' serialization stand-ins - the live objects keep their working Paths.
                ' ⚠ When adding a field to EndpointBinding, add it to this persist copy too.
                Dim persisted(EndpointBindings.Count - 1) As EndpointBinding
                For i As Integer = 0 To EndpointBindings.Count - 1
                    Dim b As EndpointBinding = EndpointBindings(i)
                    If String.IsNullOrEmpty(b.TemplateKey) Then
                        persisted(i) = b
                    Else
                        persisted(i) = New EndpointBinding With {
                            .EndpointId = b.EndpointId,
                            .TemplateKey = b.TemplateKey,
                            .Exposed = b.Exposed,
                            .AtTopLevel = b.AtTopLevel,
                            .Paths = Nothing
                        }
                    End If
                Next
                Dim data As New UPnPViewXmlData() With {
                    .Templates = BrowseTemplates.ToArray(),
                    .EndpointBindings = persisted
                }
                Using stream As New IO.FileStream(viewUrl, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.None)
                    Dim serializer As New XmlSerializer(GetType(UPnPViewXmlData))
                    serializer.Serialize(stream, data)
                End Using
            Catch ex As Exception
                LogError(ex, "View.Save", viewUrl)
            End Try
        End Sub

        ' Enumerate endpoints currently exposed: Music tree (always), all user filters
        ' (.xautopf files on disk), all MusicBee playlists (annotated with folder path so the
        ' UI can rebuild the hierarchy).
        Public Shared Function DiscoverEndpoints() As List(Of EndpointInfo)
            Dim list As New List(Of EndpointInfo)
            ' Order chosen to mirror MusicBee's left-pane: Music, then Filters (filters are
            ' part of the Music section in MusicBee), Audiobooks, Radio, Inbox, Now Playing,
            ' Playlists. The settings dialog and the root composer both rely on this order
            ' by grouping/filtering on Type - but the persisted EndpointBindings list is
            ' order-independent.
            list.Add(New EndpointInfo("music", Plugin.L("SectionMusic"), EndpointType.Music))
            Try
                Dim filtersDir As String = IO.Path.Combine( _
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _
                    "MusicBee", "Filters")
                If IO.Directory.Exists(filtersDir) Then
                    Dim files() As String = IO.Directory.GetFiles(filtersDir, "*.xautopf")
                    Array.Sort(files, StringComparer.CurrentCultureIgnoreCase)
                    For Each f As String In files
                        Dim name As String = IO.Path.GetFileNameWithoutExtension(f)
                        list.Add(New EndpointInfo("filter:" & name, name, EndpointType.Filter))
                    Next
                End If
            Catch ex As Exception
                LogError(ex, "DiscoverEndpoints.Filters")
            End Try
            ' Built-in singleton endpoints - one of each, always present. Display names use
            ' MB_GetLocalisation so the labels match what MusicBee shows in its own UI.
            list.Add(New EndpointInfo("audiobook", mbApiInterface.MB_GetLocalisation("Main.tree.AuBo", "Audiobooks"), EndpointType.Audiobook))
            list.Add(New EndpointInfo("radio", mbApiInterface.MB_GetLocalisation("Main.tree.Radio", "Radio"), EndpointType.Radio))
            list.Add(New EndpointInfo("inbox", mbApiInterface.MB_GetLocalisation("Main.tree.Inbox", "Inbox"), EndpointType.Inbox))
            list.Add(New EndpointInfo("podcast", mbApiInterface.MB_GetLocalisation("Main.tree.Podc", "Podcasts"), EndpointType.Podcast))
            list.Add(New EndpointInfo("nowplaying", mbApiInterface.MB_GetLocalisation("Main.tree.NowPlaying", "Now Playing"), EndpointType.NowPlaying))
            Try
                mbApiInterface.Playlist_QueryPlaylists()
                Do
                    Dim url As String = mbApiInterface.Playlist_QueryGetNextPlaylist()
                    If url Is Nothing Then Exit Do
                    If mbApiInterface.Playlist_GetType(url) = PlaylistFormat.Radio Then
                        Continue Do
                    End If
                    Dim fullName As String = mbApiInterface.Playlist_GetName(url)
                    Dim parts() As String = fullName.Split("\"c)
                    Dim leafName As String = parts(parts.Length - 1)
                    Dim folderPath() As String = If(parts.Length > 1, parts.Take(parts.Length - 1).ToArray(), New String() {})
                    Dim info As New EndpointInfo("playlist:" & fullName, leafName, EndpointType.Playlist)
                    info.FolderPath = folderPath
                    list.Add(info)
                Loop
            Catch ex As Exception
                LogError(ex, "DiscoverEndpoints.Playlists")
            End Try
            Return list
        End Function

        ' Walk discovered endpoints and bind anything not yet bound to its type's default
        ' template (TemplateKey reference + a stamped copy of its paths). Existing bindings
        ' are untouched - EXCEPT Radio/Podcasts, whose pairing is enforced (below).
        Public Shared Sub EnsureEndpointBindings(endpoints As List(Of EndpointInfo))
            For Each ep As EndpointInfo In endpoints
                Dim existing As EndpointBinding = EndpointBindings.FirstOrDefault(Function(b) b.EndpointId = ep.Id)
                If existing IsNot Nothing Then
                    ' The Radio and Podcasts nodes are PERMANENTLY paired with their
                    ' category's template. Enforce the link on every walk: the UI
                    ' deliberately has no way to (re)apply a Reserved template, so an
                    ' unbound or mislinked binding (e.g. one predating the reference
                    ' model) would otherwise be stuck unfixable.
                    Dim pairedKey As String = ""
                    If ep.Type = EndpointType.Radio Then pairedKey = DefaultKeyRadio
                    If ep.Type = EndpointType.Podcast Then pairedKey = DefaultKeyPodcast
                    If pairedKey <> "" AndAlso existing.TemplateKey <> pairedKey Then
                        Dim paired As BrowseTemplate = BrowseTemplates.FirstOrDefault(Function(t) t.Key = pairedKey)
                        If paired IsNot Nothing Then
                            existing.TemplateKey = pairedKey
                            existing.Paths = ClonePaths(paired.Paths)
                        End If
                    End If
                Else
                    Dim seedKey As String
                    Select Case ep.Type
                        Case EndpointType.Playlist, EndpointType.NowPlaying : seedKey = DefaultKeyTracks
                        Case EndpointType.Music : seedKey = DefaultKeyMusic
                        Case EndpointType.Radio : seedKey = DefaultKeyRadio
                        Case EndpointType.Podcast : seedKey = DefaultKeyPodcast
                        Case Else : seedKey = DefaultKeyByAlbumArtist  ' Filters, Inbox, Audiobooks
                    End Select
                    Dim seed As BrowseTemplate = BrowseTemplates.FirstOrDefault(Function(t) t.Key = seedKey)
                    ' The default may have been deleted (possible only while unused) - fall
                    ' back to the first template of the node's category so a new endpoint
                    ' never appears empty. The node's own category always has at least one
                    ' in-use (hence existing) template, except a hand-emptied file.
                    If seed Is Nothing Then
                        Dim cat As PathCategory = CategoryForEndpointType(ep.Type)
                        seed = BrowseTemplates.FirstOrDefault(Function(t) t.Category = cat)
                    End If
                    Dim stamped() As BrowsePath = If(seed IsNot Nothing, ClonePaths(seed.Paths), New BrowsePath() {})
                    EndpointBindings.Add(New EndpointBinding With {
                        .EndpointId = ep.Id,
                        .TemplateKey = If(seed IsNot Nothing, seed.Key, ""),
                        .Exposed = True,
                        .Paths = stamped
                    })
                End If
            Next
        End Sub

        ' Re-stamp every bound endpoint from its template - the propagation half of the
        ' reference model: editing a template updates every endpoint bound to it. Unbound
        ' bindings (empty TemplateKey) and dangling references (template deleted) keep
        ' their stamped Paths and simply follow nothing.
        Public Shared Sub SyncBindingsFromTemplates()
            For Each b As EndpointBinding In EndpointBindings
                If String.IsNullOrEmpty(b.TemplateKey) Then Continue For
                Dim t As BrowseTemplate = BrowseTemplates.FirstOrDefault(Function(x) x.Key = b.TemplateKey)
                If t IsNot Nothing Then b.Paths = ClonePaths(t.Paths)
            Next
        End Sub

        ' Deep-copy a path array so a binding's stamped paths never share objects with the
        ' source template - propagation is explicit (SyncBindingsFromTemplates), never an
        ' accident of shared references.
        Public Shared Function ClonePaths(source() As BrowsePath) As BrowsePath()
            If source Is Nothing Then Return New BrowsePath() {}
            Dim copy(source.Length - 1) As BrowsePath
            For i As Integer = 0 To source.Length - 1
                Dim s As BrowsePath = source(i)
                Dim hierarchyCopy() As HierarchyEntry
                If s.Hierarchy Is Nothing Then
                    hierarchyCopy = New HierarchyEntry() {}
                Else
                    ReDim hierarchyCopy(s.Hierarchy.Length - 1)
                    For h As Integer = 0 To s.Hierarchy.Length - 1
                        Dim src As HierarchyEntry = s.Hierarchy(h)
                        hierarchyCopy(h) = New HierarchyEntry(src.Field, src.BucketByLetter, src.SortDescending)
                    Next
                End If
                Dim albumGroupCopy() As AlbumGroupField
                If s.AlbumGroupBy Is Nothing Then
                    albumGroupCopy = New AlbumGroupField() {}
                Else
                    ReDim albumGroupCopy(s.AlbumGroupBy.Length - 1)
                    For g As Integer = 0 To s.AlbumGroupBy.Length - 1
                        Dim ag As AlbumGroupField = s.AlbumGroupBy(g)
                        albumGroupCopy(g) = New AlbumGroupField(ag.Field, ag.SortDescending)
                    Next
                End If
                copy(i) = New BrowsePath With {
                    .Hierarchy = hierarchyCopy,
                    .Leaf = s.Leaf,
                    .IncludeAllTracks = s.IncludeAllTracks,
                    .AlbumGroupBy = albumGroupCopy
                }
            Next
            Return copy
        End Function

        ' Look up an endpoint's binding (read-only). Returns Nothing if not yet bound.
        Public Shared Function GetBinding(endpointId As String) As EndpointBinding
            Return EndpointBindings.FirstOrDefault(Function(b) b.EndpointId = endpointId)
        End Function

        ' Auto-generated display label for a BrowsePath. Format: chain of containers the user
        ' will see when browsing, separated by " / ".
        '   []           + AT → "Album / Tracks"
        '   []           + T  → "Tracks"
        '   [Composer]   + AT → "Composer / Album / Tracks"
        '   [Genre,AA]   + AT → "Genre / Album Artist / Album / Tracks"
        ' Auto-generated label for a BrowsePath. includeSortIndicator (default False) adds "↓"
        ' suffix to any field that's sort-descending - used by the editor so the user can see
        ' sort state at a glance; UPnP browse passes False to keep client-facing labels clean.
        Public Shared Function FormatBrowsePathDisplay(p As BrowsePath) As String
            Return FormatBrowsePathDisplay(p, False)
        End Function

        Public Shared Function FormatBrowsePathDisplay(p As BrowsePath, includeSortIndicator As Boolean) As String
            If p Is Nothing Then Return ""
            Dim parts As New List(Of String)
            If p.Hierarchy IsNot Nothing Then
                For Each entry As HierarchyEntry In p.Hierarchy
                    If entry Is Nothing Then Continue For
                    If entry.BucketByLetter Then
                        parts.Add(Plugin.L("PathSegmentLetter"))
                    End If
                    Dim label As String = FieldDisplayName(entry.Field)
                    If includeSortIndicator AndAlso entry.SortDescending Then
                        label = label & "↓"
                    End If
                    parts.Add(label)
                Next
            End If
            If p.Leaf = LeafMode.AT Then
                ' The album leaf is ONE container whose identity is a composite of the album
                ' group-by fields. Render it as a single segment ("Album", or e.g.
                ' "Album Artist + Album" / "Year↓ + Album") so the composite is visible without
                ' implying nested levels (which " / " between segments would). Empty/absent
                ' AlbumGroupBy → classic "Album".
                Dim albumFields() As AlbumGroupField = p.AlbumGroupBy
                Dim albumSeg As String
                If albumFields Is Nothing OrElse albumFields.Length = 0 Then
                    albumSeg = Plugin.L("PathSegmentAlbum")
                Else
                    Dim fieldParts As New List(Of String)
                    For Each af As AlbumGroupField In albumFields
                        If af Is Nothing OrElse String.IsNullOrEmpty(af.Field) Then Continue For
                        Dim lbl As String = FieldDisplayName(af.Field)
                        If includeSortIndicator AndAlso af.SortDescending Then lbl = lbl & "↓"
                        fieldParts.Add(lbl)
                    Next
                    If fieldParts.Count = 0 Then
                        albumSeg = Plugin.L("PathSegmentAlbum")
                    Else
                        albumSeg = String.Join(" + ", fieldParts)
                    End If
                End If
                parts.Add(albumSeg)
            End If
            parts.Add(Plugin.L("PathSegmentTracks"))
            Return String.Join(" / ", parts)
        End Function

        ' Short label for UPnP-side sub-container naming when a multi-path template
        ' wraps its paths. Shows just the grouping hierarchy ("Artist", "Genre /
        ' AlbumArtist") - the trailing "/ Album / Tracks" suffix that the full
        ' display variant includes is only useful in the editor (so the user sees
        ' what the leaf will look like). In a UPnP renderer's folder list it's
        ' redundant noise.
        Public Shared Function FormatBrowsePathShortDisplay(p As BrowsePath) As String
            If p Is Nothing Then Return ""
            Dim parts As New List(Of String)
            If p.Hierarchy IsNot Nothing Then
                For Each entry As HierarchyEntry In p.Hierarchy
                    If entry Is Nothing OrElse String.IsNullOrEmpty(entry.Field) Then Continue For
                    If entry.BucketByLetter Then
                        parts.Add(Plugin.L("PathSegmentLetter"))
                    End If
                    parts.Add(FieldDisplayName(entry.Field))
                Next
            End If
            If parts.Count = 0 Then
                ' Empty-hierarchy path - fall back to leaf-shape label so it's
                ' distinguishable from siblings. (Multi-path templates SHOULD NOT
                ' include such a path per the multi-path rule, but defensive.)
                If p.Leaf = LeafMode.AT Then
                    parts.Add(Plugin.L("PathSegmentAlbum"))
                Else
                    parts.Add(Plugin.L("PathSegmentTracks"))
                End If
            End If
            Return String.Join(" / ", parts)
        End Function

        ' User-visible label for a field via MusicBee's Setting_GetFieldName (matches what the
        ' user sees in MusicBee, including their Custom/Virtual names). Falls back to the raw
        ' field name when MusicBee doesn't recognise the MetaDataType.
        Public Shared Function FieldDisplayName(fieldName As String) As String
            If String.IsNullOrEmpty(fieldName) Then Return ""
            ' Synthetic fields don't have an MB MetaDataType. Map each token to a
            ' user-friendly label that disambiguates from MB-native concepts.
            If String.Equals(fieldName, "MusicBeeFolder", StringComparison.OrdinalIgnoreCase) Then Return "MusicBee Folder"
            If SyntheticFields.Contains(fieldName) Then Return fieldName
            Dim mdt As MetaDataType = FieldNameToMetaDataType(fieldName)
            If mdt = 0 Then
                ' Tokens whose tag code the MB-API enum omits but Setting_GetFieldName still
                ' labels (e.g. YearOnly = 35 → "Year (yyyy)"). ItemManager's MetaDataType is a
                ' superset that names them; cast to the MB-API enum for the label lookup.
                Dim superMdt As ItemManager.MetaDataType
                If [Enum].TryParse(Of ItemManager.MetaDataType)(fieldName, True, superMdt) AndAlso [Enum].IsDefined(GetType(ItemManager.MetaDataType), superMdt) Then
                    mdt = CType(superMdt, MetaDataType)
                End If
            End If
            If mdt <> 0 Then
                Try
                    Dim label As String = mbApiInterface.Setting_GetFieldName(mdt)
                    If Not String.IsNullOrEmpty(label) Then Return label
                Catch
                End Try
            End If
            Return fieldName
        End Function

        ' String field name → Plugin.MetaDataType via Enum.TryParse. Returns 0 for unknowns.
        Public Shared Function FieldNameToMetaDataType(fieldName As String) As MetaDataType
            If String.IsNullOrEmpty(fieldName) Then Return 0
            Dim result As MetaDataType
            If [Enum].TryParse(Of MetaDataType)(fieldName, result) Then
                Return result
            End If
            Return 0
        End Function
    End Class  ' View
End Class
