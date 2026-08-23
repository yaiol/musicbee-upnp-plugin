Imports System.Drawing
Imports System.Threading
Imports System.Xml.Linq

Partial Friend NotInheritable Class SettingsDialog
    Inherits System.Windows.Forms.Form
    Private isLoadComplete As Boolean = False
    Private isDirty As Boolean = False
    Private lastProfileIndex As Integer = -1
    Private lastPictureSize As String
    Private bandwidthWarningDisplayed As Boolean = False

    ' Diagnostic helper that appends a line to %AppData%\MusicBee\UPnPCrashTrace.txt
    ' regardless of LogDebugInfo state. Used to pin which stage of the dialog ctor crashes
    ' when MusicBee swallows the exception. Best-effort: any failure inside silently no-ops.
    Private Shared Sub CrashTrace(message As String)
        Try
            Dim path As String = Plugin.mbApiInterface.Setting_GetPersistentStoragePath() & "UPnPCrashTrace.txt"
            IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") & " " & message & Environment.NewLine)
        Catch
        End Try
    End Sub

    Public Sub New()
        CrashTrace("ctor: start")
        Try
            InitializeComponent()
            CrashTrace("ctor: InitializeComponent OK")
        Catch ex As Exception
            CrashTrace("ctor: InitializeComponent FAILED: " & ex.ToString())
            Throw
        End Try
        ' Re-detect MusicBee's UI language and reload the string bundle every time the dialog opens,
        ' so the plugin follows a MusicBee language change without a MusicBee restart. (Localisation
        ' is also applied once at plugin Initialise; that snapshot goes stale if the user switched
        ' MusicBee's language since startup - this re-reads the current MusicBee3Settings.ini.)
        Plugin.Localisation.Apply()
        ApplyDesignerExtras()
        AddHandler chkEnableController.CheckedChanged, AddressOf chkEnableController_CheckedChanged
        'AddHandler continuousStream.CheckedChanged, AddressOf continuousStream_CheckedChanged
        AddHandler lstDevStreamingProfiles.SelectedIndexChanged, AddressOf lstDevStreamingProfiles_SelectedIndexChanged
        AddHandler chkSrvEnableBrowse.CheckedChanged, AddressOf chkSrvEnableBrowse_CheckedChanged
        AddHandler chkPbkBandwidthIsConstrained.CheckedChanged, AddressOf chkPbkBandwidthIsConstrained_CheckedChanged
        AddHandler chkDbgInfo.CheckedChanged, AddressOf chkDbgInfo_CheckedChanged
        Me.Font = Plugin.mbApiInterface.Setting_GetDefaultFont()
        Dim boldFont As New Font(Me.Font, FontStyle.Bold)
        ' "Server settings…" is the only genuine section header here; the three role checkboxes
        ' below are peers and render in the normal font (their old bold-header styling is dropped).
        Me.lblSrvSettings.Font = boldFont
        ' ⚠ CLAUDE: a section header must derive its bold FROM Me.Font, never hardcode a family
        ' or size. Me.Font is MusicBee's own UI font (set just above), so a designer-baked
        ' "Microsoft Sans Serif, 7.8pt, Bold" does not follow it - the label keeps the design-time
        ' font while every other control switches, and it reads as a foreign typeface at runtime.
        ' These two carried exactly that and looked wrong on screen while looking identical in the
        ' VS designer, which is what made it hard to see. Same rule for any new header.
        Me.lblDevCapabilities.Font = boldFont
        Me.lblDevProblems.Font = boldFont
        Me.chkEnableController.Checked = Plugin.Settings.EnablePlayToDevice
        Me.chkEnableMediaRenderer.Checked = Plugin.Settings.EnableMediaRenderer
        Me.chkPbkContinuousStream.Checked = Plugin.Settings.ContinuousOutput
        Me.txtSrvName.Text = Plugin.Settings.ServerName
        Me.txtRendererName.Text = Plugin.Settings.RendererName
        Me.cboSrvIpAddress.Items.Add("Automatic")
        Me.cboSrvIpAddress.SelectedIndex = 0
        For index As Integer = 0 To Plugin.hostAddresses.Length - 1
            Dim address As String = Plugin.hostAddresses(index).ToString()
            Me.cboSrvIpAddress.Items.Add(address)
            If address = Plugin.Settings.IpAddress Then
                Me.cboSrvIpAddress.SelectedIndex = index + 1
            End If
        Next index
        If Not String.IsNullOrEmpty(Plugin.Settings.IpAddress) AndAlso Me.cboSrvIpAddress.SelectedIndex = 0 Then
            Me.cboSrvIpAddress.Items.Add(Plugin.Settings.IpAddress)
            Me.cboSrvIpAddress.SelectedIndex = Me.cboSrvIpAddress.Items.Count - 1
        End If
        Me.cboSrvIpAddress.MaxDropDownItems = Me.cboSrvIpAddress.Items.Count
        Me.txtSrvPort.Text = Plugin.Settings.ServerPort.ToString()
        ' F45 - clamp into the NumericUpDown's allowed range before assigning so a corrupted
        ' setting can't throw.
        Me.numSrvMaxConnections.Value = Math.Min(Me.numSrvMaxConnections.Maximum, Math.Max(Me.numSrvMaxConnections.Minimum, CDec(Plugin.Settings.MaxConnections)))
        Me.cboDevSampleRateFrom.Items.AddRange(New String() {"11025", "22050", "44100", "48000", "88200", "96000", "176400", "192000", "2822400"})
        Me.cboDevSampleRateTo.Items.AddRange(New String() {"11025", "22050", "44100", "48000", "88200", "96000", "176400", "192000", "2822400"})
        Me.cboDevTranscodeSampleRate.Items.AddRange(New String() {"11025", "22050", "44100", "48000", "88200", "96000", "176400", "192000", "2822400", Plugin.L("TranscodeSampleRateSameAsSource")})
        Me.cboDevMaxBitDepth.Items.AddRange(New String() {"16", "24"})
        Me.cboDevTranscodeFormat.Items.AddRange(New String() {"PCM - 16 bit", "PCM - 24 bit", "MP3", "AAC", "Ogg", "FLAC"})
        Me.lstDevStreamingProfiles.BeginUpdate()
        For index As Integer = 0 To Plugin.Settings.StreamingProfiles.Count - 1
            Me.lstDevStreamingProfiles.Items.Add(Plugin.Settings.StreamingProfiles(index))
        Next index
        Me.lstDevStreamingProfiles.SelectedIndex = Plugin.Settings.DefaultProfileIndex
        Me.lstDevStreamingProfiles.EndUpdate()
        Me.chkSrvEnableBrowse.Checked = Plugin.Settings.EnableContentAccess
        Me.chkLibOptSubmitPlayStats.Checked = Plugin.Settings.ServerUpdatePlayStatistics
        ' WYSIWYG prefixes prepended to filter/playlist names when pinned to the browse root.
        Me.txtLibOptFilterPrefix.Text = If(Plugin.Settings.FilterPrefix, "")
        Me.txtLibOptPlaylistPrefix.Text = If(Plugin.Settings.PlaylistPrefix, "")
        ' Random source picker - "(All Music)" first, then every MusicBee filter. Same shape as
        ' cboSrvIpAddress above: filled here, read back in the Save handler, no event handler.
        ' Item 0 is the empty (whole-library) value, so SelectedIndex 0 <=> "".
        Me.cboLibOptRandomSource.Items.Add(Plugin.L("cboLibOptRandomSourceAll"))
        Me.cboLibOptRandomSource.SelectedIndex = 0
        Dim savedRandomSource As String = If(Plugin.Settings.RandomSourceFilter, "")
        For Each filterName As String In Plugin.ItemManager.RandomSourceFilterNames()
            Dim addedAt As Integer = Me.cboLibOptRandomSource.Items.Add(filterName)
            If String.Equals(filterName, savedRandomSource, StringComparison.OrdinalIgnoreCase) Then
                Me.cboLibOptRandomSource.SelectedIndex = addedAt
            End If
        Next
        ' A saved filter whose .xautopf has since been deleted or renamed is NOT re-added: it
        ' selects nothing, so the picker shows "All Music" and saving clears the setting. That
        ' is deliberate - you cannot draw a random pick from a filter that no longer exists, so
        ' offering it would be a dead choice. It matches the runtime, where RandomSourceEndpoint
        ' falls back to the whole library for exactly the same reason.
        LoadLibOptHierFields()
        ' EQ/DSP and ReplayGain are now per-profile - LoadProfile() wires them when a profile is selected.
        ' Filter/playlist hide is now part of the Views tab (Visibility checkbox).
        Me.chkPbkBandwidthIsConstrained.Checked = Plugin.Settings.BandwidthConstrained
        Me.chkPbkForceNativeStreamForRadio.Checked = Plugin.Settings.ForceNativeStreamForRadio
        Me.chkDbgInfo.Checked = Plugin.Settings.LogDebugInfo
        Me.chkDbgClearLogOnStartup.Checked = Plugin.Settings.ClearLogOnStartup
        Me.btnDbgView.Left = Me.chkDbgInfo.Right + 5
        Try
            LoadViewsTab()
            CrashTrace("ctor: LoadViewsTab OK")
        Catch ex As Exception
            CrashTrace("ctor: LoadViewsTab FAILED: " & ex.ToString())
            Throw
        End Try
        isLoadComplete = True
        UpdateRoleTabs()
        AddHandler btnDevAddProfile.Click, AddressOf btnDevAddProfile_Click
        ' F40 - when continuous-stream is ticked, auto-untick force-native-stream on the active
        ' profile (they're incompatible: continuous-stream always transcodes). The user can
        ' re-tick force-native-stream later if they untick continuous-stream.
        AddHandler Me.chkPbkContinuousStream.CheckedChanged, AddressOf chkPbkContinuousStream_CheckedChanged
        AddHandler btnDevRemoveProfile.Click, AddressOf btnDevRemoveProfile_Click
        AddHandler txtDevPictureSize.Leave, AddressOf txtDevPictureSize_Leave
        AddHandler btnDbgView.Click, AddressOf btnDbgView_Click
        AddHandler btnDbgClearLog.Click, AddressOf btnDbgClearLog_Click
        AddHandler btnDbgReadLastQuery.Click, AddressOf btnDbgReadLastQuery_Click
        AddHandler btnDbgXmlRun.Click, AddressOf btnDbgXmlRun_Click
        AddHandler btnDbgXmlFetchTags.Click, AddressOf btnDbgXmlFetchTags_Click
        AddHandler btnSave.Click, AddressOf btnSave_Click
        AddHandler btnClose.Click, AddressOf btnClose_Click
        AddHandler btnHelp.Click, AddressOf btnHelp_Click
        AddHandler btnGithub.Click, AddressOf btnGithub_Click
        AddHandler Me.lnkUpdateWhatsNew.LinkClicked, AddressOf lnkUpdateWhatsNew_LinkClicked
        AddHandler Me.lnkUpdateDownload.LinkClicked, AddressOf lnkUpdateDownload_LinkClicked
        StartUpdateCheck()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        MyBase.Dispose(disposing)
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If isDirty Then
            Select Case MessageBox.Show(Me, Plugin.L("ConfirmSaveAmendments"), Plugin.L("MessageBoxTitle"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1)
                Case DialogResult.Cancel
                    e.Cancel = True
                Case Windows.Forms.DialogResult.OK
                    SaveSettings()
            End Select
        End If
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        If Not Plugin.ipOverrideAddressMatched Then
            MessageBox.Show(Me, String.Format(Plugin.L("WarnIpAddressNotOperational"), Plugin.Settings.IpAddress), Plugin.L("MessageBoxTitle"))
        End If
    End Sub

    Private Sub chkEnableController_CheckedChanged(sender As Object, e As EventArgs)
        Dim enabled As Boolean = Me.chkEnableController.Checked
        Me.chkPbkContinuousStream.Enabled = enabled
        Me.lblPbkContinuousStream.Enabled = enabled
        UpdateRoleTabs()
    End Sub

    ' F40 - continuous-stream is mutually exclusive with force-native-stream (the latter sends
    ' file bytes unchanged; the former ALWAYS transcodes). Toggling continuous-stream ON unticks
    ' the active profile's force-native-stream so the user sees the immediate effect; the
    ' setting is saved when they hit Save and applies to that profile only.
    Private Sub chkPbkContinuousStream_CheckedChanged(sender As Object, e As EventArgs)
        If Me.chkPbkContinuousStream.Checked AndAlso Me.chkDevForceNativeStream.Checked Then
            ' Use the mutual-exclusion handler so the F4 ForceTranscoding interlock fires correctly.
            Me.chkDevForceNativeStream.Checked = False
        End If
    End Sub

    Private Sub chkSrvEnableBrowse_CheckedChanged(sender As Object, e As EventArgs)
        Dim enabled As Boolean = Me.chkSrvEnableBrowse.Checked
        Me.chkLibOptSubmitPlayStats.Enabled = enabled
        ' EQ/DSP and ReplayGain moved to per-profile (Device Profiles section).
        UpdateRoleTabs()
    End Sub

    ' Show/hide top-level tabs by which UPnP roles are enabled. Runtime only - the TabPage objects
    ' (and everything on them) persist; they're just removed from / re-added to the TabControl in
    ' canonical order. Role → tabs:
    '   Server (browse)          → Library Options, Library Paths
    '   Server OR Control Point  → Playback, Device Profiles (both serve/transcode streams)
    '   General, Debug           → always
    ' (The Renderer role gates no tab - loopback playback uses none of these settings.)
    Private Sub UpdateRoleTabs()
        Dim server As Boolean = Me.chkSrvEnableBrowse.Checked
        Dim controlPoint As Boolean = Me.chkEnableController.Checked
        Dim ordered() As System.Windows.Forms.TabPage = {Me.tabGeneral, Me.tabProfiles, Me.tabPlayback, Me.tabLibraryGeneral, Me.tabLibraryPaths, Me.tabDebug}
        Dim selected As System.Windows.Forms.TabPage = Me.tbcMain.SelectedTab
        Me.tbcMain.SuspendLayout()
        Me.tbcMain.TabPages.Clear()
        For Each page As System.Windows.Forms.TabPage In ordered
            Dim show As Boolean
            If page Is Me.tabProfiles OrElse page Is Me.tabPlayback Then
                show = server OrElse controlPoint
            ElseIf page Is Me.tabLibraryGeneral OrElse page Is Me.tabLibraryPaths Then
                show = server
            Else
                show = True
            End If
            If show Then Me.tbcMain.TabPages.Add(page)
        Next page
        If selected IsNot Nothing AndAlso Me.tbcMain.TabPages.Contains(selected) Then
            Me.tbcMain.SelectedTab = selected
        End If
        Me.tbcMain.ResumeLayout()
    End Sub

    Private Sub chkPbkBandwidthIsConstrained_CheckedChanged(sender As Object, e As EventArgs)
        If isLoadComplete AndAlso Me.chkPbkBandwidthIsConstrained.Checked AndAlso Not bandwidthWarningDisplayed AndAlso Me.cboDevTranscodeFormat.SelectedIndex < 2 Then
            bandwidthWarningDisplayed = True
            MessageBox.Show(Me, Plugin.L("WarnBandwidth"), Plugin.L("MessageBoxTitle"), MessageBoxButtons.OK, MessageBoxIcon.Exclamation)
            Me.cboDevTranscodeFormat.SelectedIndex = 2
        End If
    End Sub

    Private Sub chkDbgInfo_CheckedChanged(sender As Object, e As EventArgs)
        Me.btnDbgView.Visible = Me.chkDbgInfo.Checked
    End Sub

    ' F4/F37 - mutual exclusion between ForceNativeStream and ForceTranscoding. Ticking one
    ' un-ticks the other so the user can't end up in the contradictory state where the runtime
    ' has to choose which wins (it would be ForceTranscoding, but the UI shouldn't show both).
    ' The Checked-setter inside fires CheckedChanged again, so we unsubscribe-during-update to
    ' avoid an infinite loop.
    Private Sub chkDevForceNativeStream_CheckedChanged(sender As Object, e As EventArgs)
        If Me.chkDevForceNativeStream.Checked AndAlso Me.chkDevForceTranscoding.Checked Then
            RemoveHandler Me.chkDevForceTranscoding.CheckedChanged, AddressOf chkDevForceTranscoding_CheckedChanged
            Me.chkDevForceTranscoding.Checked = False
            AddHandler Me.chkDevForceTranscoding.CheckedChanged, AddressOf chkDevForceTranscoding_CheckedChanged
        End If
    End Sub

    Private Sub chkDevForceTranscoding_CheckedChanged(sender As Object, e As EventArgs)
        If Me.chkDevForceTranscoding.Checked AndAlso Me.chkDevForceNativeStream.Checked Then
            RemoveHandler Me.chkDevForceNativeStream.CheckedChanged, AddressOf chkDevForceNativeStream_CheckedChanged
            Me.chkDevForceNativeStream.Checked = False
            AddHandler Me.chkDevForceNativeStream.CheckedChanged, AddressOf chkDevForceNativeStream_CheckedChanged
        End If
    End Sub

    ' ── Hierarchical fields (Library Options) ──────────────────────────────────────
    ' Edits a working copy of the global field→delimiter map; committed to Settings on Save.
    Private libOptHierMap As Dictionary(Of String, String)
    Private libOptHierKeys As List(Of String)

    Private Sub LoadLibOptHierFields()
        libOptHierMap = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For Each kv As KeyValuePair(Of String, String) In Plugin.Settings.HierarchicalFields
            libOptHierMap(kv.Key) = kv.Value
        Next
        RefreshLibOptHierList()
    End Sub

    Private Sub RefreshLibOptHierList()
        Me.lstLibOptHierFields.Items.Clear()
        libOptHierKeys = New List(Of String)
        For Each kv As KeyValuePair(Of String, String) In libOptHierMap
            libOptHierKeys.Add(kv.Key)
            Me.lstLibOptHierFields.Items.Add(Plugin.View.FieldDisplayName(kv.Key) & "   " & ChrW(&H2192) & "   " & kv.Value)
        Next
    End Sub

    Private Sub libOptHierAdd_Click(sender As Object, e As EventArgs)
        Dim f As String = Me.fpcLibOptHierField.SelectedField
        Dim c As String = If(Me.txtLibOptHierChar.Text, "")
        If String.IsNullOrEmpty(f) OrElse c.Length = 0 Then Return
        c = c.Substring(0, 1)
        If c = ";" Then Return  ' ";" is MusicBee's multi-value separator - not a valid tree delimiter
        If libOptHierMap Is Nothing Then libOptHierMap = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        libOptHierMap(f) = c
        RefreshLibOptHierList()
        Me.txtLibOptHierChar.Clear()
    End Sub

    Private Sub libOptHierRemove_Click(sender As Object, e As EventArgs)
        Dim idx As Integer = Me.lstLibOptHierFields.SelectedIndex
        If idx < 0 OrElse libOptHierKeys Is Nothing OrElse idx >= libOptHierKeys.Count Then Return
        libOptHierMap.Remove(libOptHierKeys(idx))
        RefreshLibOptHierList()
    End Sub

    Private Sub libOptHierList_SelectionChanged(sender As Object, e As EventArgs)
        Dim idx As Integer = Me.lstLibOptHierFields.SelectedIndex
        If idx < 0 OrElse libOptHierKeys Is Nothing OrElse idx >= libOptHierKeys.Count Then Return
        Dim key As String = libOptHierKeys(idx)
        Me.fpcLibOptHierField.SetSelectedField(key)
        Me.txtLibOptHierChar.Text = libOptHierMap(key)
    End Sub

    Private Sub lstDevStreamingProfiles_SelectedIndexChanged(sender As Object, e As EventArgs)
        Dim profile As Plugin.StreamingProfile
        If lastProfileIndex <> -1 Then
            RemoveHandler lstDevStreamingProfiles.SelectedIndexChanged, AddressOf lstDevStreamingProfiles_SelectedIndexChanged
            Me.lstDevStreamingProfiles.Items(lastProfileIndex) = UpdateCurrentStreamingProfile()
            AddHandler lstDevStreamingProfiles.SelectedIndexChanged, AddressOf lstDevStreamingProfiles_SelectedIndexChanged
        End If
        lastProfileIndex = Me.lstDevStreamingProfiles.SelectedIndex
        If lastProfileIndex <> -1 Then
            profile = DirectCast(Me.lstDevStreamingProfiles.SelectedItem, Plugin.StreamingProfile)
            LoadProfile(profile)
            Me.txtDevProfileName.Enabled = (lastProfileIndex > 0)
            Me.txtDevUserAgent.Enabled = (lastProfileIndex > 0)
        End If
        Me.btnDevRemoveProfile.Enabled = (lastProfileIndex > 0)
    End Sub

    Private Sub LoadProfile(profile As Plugin.StreamingProfile)
        Me.txtDevProfileName.Text = profile.ProfileName
        Dim value As String = ""
        For index As Integer = 0 To profile.UserAgents.Length - 1
            If index > 0 Then
                value &= "|"
            End If
            value &= profile.UserAgents(index)
        Next index
        Me.txtDevUserAgent.Text = value
        Me.txtDevPictureSize.Text = profile.PictureSize.ToString()
        lastPictureSize = Me.txtDevPictureSize.Text
        Me.cboDevSampleRateFrom.SelectedIndex = GetSampleRateIndex(profile.MinimumSampleRate)
        Me.cboDevSampleRateTo.SelectedIndex = GetSampleRateIndex(profile.MaximumSampleRate)
        Me.chkDevStereoOnly.Checked = profile.StereoOnly
        Me.cboDevMaxBitDepth.SelectedIndex = If(profile.MaximumBitDepth <> 24, 0, 1)
        Select Case profile.TranscodeCodec
            Case Plugin.FileCodec.Pcm
                Me.cboDevTranscodeFormat.SelectedIndex = If(profile.TranscodeBitDepth <> 24, 0, 1)
            Case Plugin.FileCodec.Mp3
                Me.cboDevTranscodeFormat.SelectedIndex = 2
            Case Plugin.FileCodec.Aac
                Me.cboDevTranscodeFormat.SelectedIndex = 3
            Case Plugin.FileCodec.Ogg
                Me.cboDevTranscodeFormat.SelectedIndex = 4
            Case Plugin.FileCodec.Flac
                Me.cboDevTranscodeFormat.SelectedIndex = 5
            Case Else
                Me.cboDevTranscodeFormat.SelectedIndex = 0
        End Select
        Me.cboDevTranscodeSampleRate.SelectedIndex = GetSampleRateIndex(profile.TranscodeSampleRate)
        Me.chkDevDoNotUseRawPcm.Checked = profile.DoNotUseRawPcm
        Me.chkDevForceLittleEndianPcm.Checked = profile.ForceLittleEndianPcm
        Me.cboDevContentLength.SelectedIndex = CInt(profile.ContentLength)
        Me.chkDevForceNativeStream.Checked = profile.ForceNativeStream
        Me.chkDevForceTranscoding.Checked = profile.ForceTranscoding
        Me.chkDevEnableNextUri.Checked = profile.EnableNextUri
        Me.chkDevDoNotClearNextUri.Checked = profile.DoNotClearNextUri
        Me.chkDevEnableSoundEffects.Checked = profile.EnableSoundEffects
        Me.chkDevEnableReplayGain.Checked = profile.EnableReplayGain
    End Sub

    Private Function GetSampleRateIndex(value As Integer) As Integer
        Select Case value
            Case 11025
                Return 0
            Case 22050
                Return 1
            Case 48000
                Return 3
            Case 88200
                Return 4
            Case 96000
                Return 5
            Case 176400
                Return 6
            Case 192000
                Return 7
            Case 2822400
                Return 8
            Case -1
                Return 9
            Case Else
                Return 2
        End Select
    End Function

    ' ========================================================================
    ' Browse-views - Library → Views tab
    ' ========================================================================

    ' P/Invoke for enabling native double-buffering on the TreeView. Without this, the
    ' state image redraw on mouse-over causes visible flicker. TVS_EX_DOUBLEBUFFER is
    ' the supported flag (Vista+) and has to be set after the handle exists.
    <System.Runtime.InteropServices.DllImport("user32.dll")>
    Private Shared Function SendMessage(hWnd As IntPtr, msg As Integer, wParam As IntPtr, lParam As IntPtr) As IntPtr
    End Function
    Private Const TVM_SETEXTENDEDSTYLE As Integer = &H1100 + 44
    Private Const TVS_EX_DOUBLEBUFFER As Integer = &H4

    Private Sub trvLibViwEndpoints_HandleCreated(sender As Object, e As EventArgs)
        SendMessage(Me.trvLibViwEndpoints.Handle, TVM_SETEXTENDEDSTYLE, CType(TVS_EX_DOUBLEBUFFER, IntPtr), CType(TVS_EX_DOUBLEBUFFER, IntPtr))
    End Sub

    ' The two action blocks on the Library Paths tab sit outside the inner Paths/View sub-tab
    ' control, so without gating they'd show on both sub-tabs. Bind each block to its own sub-tab:
    '   • Paths sub-tab → template management (New / Delete / ↑ / ↓)
    '   • View  sub-tab → Apply + Visibility / Top level
    Private Sub tbcLibraryPaths_SelectedIndexChanged(sender As Object, e As EventArgs)
        UpdateLibraryPathsTabButtons()
    End Sub

    Private Sub UpdateLibraryPathsTabButtons()
        Dim onView As Boolean = (Me.tbcLibraryPaths.SelectedTab Is Me.tabUPnPView)
        Me.btnLibPthTemplateNew.Visible = Not onView
        Me.btnLibPthTemplateDelete.Visible = Not onView
        Me.btnLibPthTemplateUp.Visible = Not onView
        Me.btnLibPthTemplateDown.Visible = Not onView
        Me.btnLibViwApply.Visible = onView
        Me.chkLibViwVisible.Visible = onView
        Me.chkLibViwTopLevel.Visible = onView
    End Sub

    Private Sub btnLibViwExpandAll_Click(sender As Object, e As EventArgs)
        Me.trvLibViwEndpoints.ExpandAll()
    End Sub

    Private Sub btnLibViwCollapseAll_Click(sender As Object, e As EventArgs)
        Me.trvLibViwEndpoints.CollapseAll()
    End Sub

    ' ── Template filter (funnel) - toggle: show only the endpoints following the template
    ' selected in the list. Selecting another template while active re-filters; toggling
    ' off restores the full tree. Filtering only ever touches the endpoint TREE
    ' (RebuildEndpointTree + prune) - never the template list or the path editor, so
    ' nothing visible on the Paths sub-tab moves.
    Private viewsFilterActive As Boolean = False

    ' btnLibViwFilter is a CheckBox with Appearance.Button - Windows renders the
    ' checked (filter-on) state natively, so no manual pressed-look painting.
    Private Sub btnLibViwFilter_Click(sender As Object, e As EventArgs)
        viewsFilterActive = Me.btnLibViwFilter.Checked
        If viewsFilterActive Then
            ApplyViewTreeFilter()
        Else
            RebuildFilteredTree()
        End If
    End Sub

    ' Full tree rebuild, re-pruned when the filter is on. The only way back from a pruned
    ' tree to the full one. Tree-only - the template list and path editor are untouched.
    Private Sub RebuildFilteredTree()
        Me.trvLibViwEndpoints.BeginUpdate()
        Try
            RebuildEndpointTree()
            If viewsFilterActive Then ApplyViewTreeFilter()
        Finally
            Me.trvLibViwEndpoints.EndUpdate()
        End Try
    End Sub

    ' Prune the current tree to the endpoints following the selected template. Group and
    ' folder nodes left with no endpoint underneath are removed too. Endpoint nodes are
    ' never recursed into (their children are the informational path rows).
    Private Sub ApplyViewTreeFilter()
        If viewsEditingTemplate Is Nothing Then Return
        Me.trvLibViwEndpoints.BeginUpdate()
        Try
            PruneToTemplate(Me.trvLibViwEndpoints.Nodes, viewsEditingTemplate.Key)
        Finally
            Me.trvLibViwEndpoints.EndUpdate()
        End Try
    End Sub

    ' Removes non-matching endpoints and emptied groups; returns how many endpoints survive
    ' in this collection's subtree.
    Private Function PruneToTemplate(nodes As System.Windows.Forms.TreeNodeCollection, key As String) As Integer
        Dim kept As Integer = 0
        For i As Integer = nodes.Count - 1 To 0 Step -1
            Dim n As System.Windows.Forms.TreeNode = nodes(i)
            Dim info As Plugin.EndpointInfo = TryCast(n.Tag, Plugin.EndpointInfo)
            If info IsNot Nothing Then
                Dim b As Plugin.EndpointBinding = Plugin.View.GetBinding(info.Id)
                If b IsNot Nothing AndAlso b.TemplateKey = key Then
                    kept += 1
                Else
                    nodes.RemoveAt(i)
                End If
            ElseIf n.Tag IsNot Nothing Then
                Dim childKept As Integer = PruneToTemplate(n.Nodes, key)
                If childKept = 0 Then
                    nodes.RemoveAt(i)
                Else
                    kept += childKept
                End If
            End If
        Next
        Return kept
    End Function

    ' Sentinel Tag values to distinguish checkable group/folder nodes from path-children.
    ' Endpoint nodes carry an Plugin.EndpointInfo as Tag (TypeOf Is Plugin.EndpointInfo). Path-children
    ' carry Nothing (un-checkable display rows). Everything else is checkable.
    Private Const TagFiltersGroup As String = "group:filters"
    Private Const TagPlaylistsGroup As String = "group:playlists"
    Private Const TagFolderGroup As String = "group:folder"

    ' StateImageList indices for the tri-state checkbox display.
    Private Const StateUnchecked As Integer = 0
    Private Const StateChecked As Integer = 1
    Private Const StateMixed As Integer = 2
    ' Text prefix marking an endpoint pinned to the browse root (binding.AtTopLevel). Shown
    ' in the node label because the node's one image slot is the selection checkbox.
    Private Const TopLevelMarker As String = "★ "

    ' Build the 3-bitmap state image list used by the TreeView. Drawn via CheckBoxRenderer
    ' so the glyphs match the OS theme (Vista+ Aero, Win10/11 modern, classic, etc.).
    Private Function BuildViewsStateImageList() As System.Windows.Forms.ImageList
        Dim list As New System.Windows.Forms.ImageList() With {
            .ImageSize = New System.Drawing.Size(16, 16),
            .ColorDepth = System.Windows.Forms.ColorDepth.Depth32Bit
        }
        Dim states() As System.Windows.Forms.VisualStyles.CheckBoxState = New System.Windows.Forms.VisualStyles.CheckBoxState() {
            System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal,
            System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal,
            System.Windows.Forms.VisualStyles.CheckBoxState.MixedNormal
        }
        For Each st As System.Windows.Forms.VisualStyles.CheckBoxState In states
            Dim bmp As New System.Drawing.Bitmap(16, 16)
            Using g As System.Drawing.Graphics = System.Drawing.Graphics.FromImage(bmp)
                g.Clear(System.Drawing.Color.Transparent)
                System.Windows.Forms.CheckBoxRenderer.DrawCheckBox(g, New System.Drawing.Point(0, 0), st)
            End Using
            list.Images.Add(bmp)
        Next
        Return list
    End Function

    ' Populate the endpoints TreeView from the live MusicBee state. Tree shape:
    '   Music                         (single endpoint, top-level)
    '   Filters/                      (checkable parent group)
    '     ├─ <filter>                 (endpoint)
    '     │   └─ <path rows>          (path-children - informational only, no checkbox)
    '     └─ ...
    '   Playlists/                    (checkable parent group)
    '     ├─ <folder>                 (checkable folder group, nested as in MusicBee)
    '     │   └─ <playlist>           (endpoint)
    '     └─ ...
    '
    ' Endpoint nodes get a state image (0=unchecked / 1=checked / 2=mixed). Hidden endpoints
    ' (binding.Exposed = False) also render in grey text. Parent groups derive their state
    ' from their descendants - all-checked = checked, all-unchecked = unchecked, mixed = mixed.
    Private Sub LoadViewsTab()
        RebuildEndpointTree()
        ' Reset the template list; its selection-changed handler reloads the editor.
        RefreshTemplateCombo(Nothing)
    End Sub

    ' Tree-only rebuild: discovers endpoints and repopulates the endpoint TreeView WITHOUT
    ' touching the template list or the path editor. The template-filter paths call this
    ' directly - going through the combo just to refresh the tree bounced the list
    ' selection and redrew the whole path editor (visible flicker on the Paths sub-tab).
    Private Sub RebuildEndpointTree()
        viewsSuppressEvents = True
        Try
            ' Discover endpoints + ensure each has a binding (stamping Default templates as needed).
            Dim endpoints As List(Of Plugin.EndpointInfo) = Plugin.View.DiscoverEndpoints()
            Plugin.View.EnsureEndpointBindings(endpoints)
            ' Ensure StateImageList is wired exactly once (first call).
            If Me.trvLibViwEndpoints.StateImageList Is Nothing Then
                Me.trvLibViwEndpoints.StateImageList = BuildViewsStateImageList()
            End If
            Me.trvLibViwEndpoints.Nodes.Clear()
            ' Tree order mirrors MusicBee's left pane: Music, Filters (part of Music in
            ' MusicBee), Audiobooks, Radio, Inbox, Podcasts, Now Playing, Playlists.
            ' 1. Music endpoint.
            Dim musicEp As Plugin.EndpointInfo = endpoints.FirstOrDefault(Function(e) e.Type = Plugin.EndpointType.Music)
            If musicEp IsNot Nothing Then
                Me.trvLibViwEndpoints.Nodes.Add(BuildEndpointNode(musicEp))
            End If
            ' 2. Filters group + its filter endpoints (sorted by display name, case-insensitive).
            Dim filterGroup As New System.Windows.Forms.TreeNode(Plugin.L("FiltersFolderName")) With {
                .Tag = TagFiltersGroup
            }
            Me.trvLibViwEndpoints.Nodes.Add(filterGroup)
            Dim filterEps As List(Of Plugin.EndpointInfo) = endpoints.Where(Function(e) e.Type = Plugin.EndpointType.Filter) _
                .OrderBy(Function(e) e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList()
            For Each ep As Plugin.EndpointInfo In filterEps
                filterGroup.Nodes.Add(BuildEndpointNode(ep))
            Next
            ' 3. Audiobooks endpoint.
            Dim audiobookEp As Plugin.EndpointInfo = endpoints.FirstOrDefault(Function(e) e.Type = Plugin.EndpointType.Audiobook)
            If audiobookEp IsNot Nothing Then
                Me.trvLibViwEndpoints.Nodes.Add(BuildEndpointNode(audiobookEp))
            End If
            ' 4. Radio endpoint.
            Dim radioEp As Plugin.EndpointInfo = endpoints.FirstOrDefault(Function(e) e.Type = Plugin.EndpointType.Radio)
            If radioEp IsNot Nothing Then
                Me.trvLibViwEndpoints.Nodes.Add(BuildEndpointNode(radioEp))
            End If
            ' 5. Inbox endpoint.
            Dim inboxEp As Plugin.EndpointInfo = endpoints.FirstOrDefault(Function(e) e.Type = Plugin.EndpointType.Inbox)
            If inboxEp IsNot Nothing Then
                Me.trvLibViwEndpoints.Nodes.Add(BuildEndpointNode(inboxEp))
            End If
            ' 6. Podcasts endpoint.
            Dim podcastEp As Plugin.EndpointInfo = endpoints.FirstOrDefault(Function(e) e.Type = Plugin.EndpointType.Podcast)
            If podcastEp IsNot Nothing Then
                Me.trvLibViwEndpoints.Nodes.Add(BuildEndpointNode(podcastEp))
            End If
            ' 7. Now Playing endpoint.
            Dim nowPlayingEp As Plugin.EndpointInfo = endpoints.FirstOrDefault(Function(e) e.Type = Plugin.EndpointType.NowPlaying)
            If nowPlayingEp IsNot Nothing Then
                Me.trvLibViwEndpoints.Nodes.Add(BuildEndpointNode(nowPlayingEp))
            End If
            ' 6. Playlists group + folder hierarchy + leaf playlist endpoints.
            Dim playlistGroup As New System.Windows.Forms.TreeNode(Plugin.L("PlaylistsFolderName")) With {
                .Tag = TagPlaylistsGroup
            }
            Me.trvLibViwEndpoints.Nodes.Add(playlistGroup)
            Dim playlistEps As List(Of Plugin.EndpointInfo) = endpoints.Where(Function(e) e.Type = Plugin.EndpointType.Playlist).ToList()
            ' Folder lookup is keyed by cumulative path under the Playlists group so siblings
            ' with the same name at different depths don't collide.
            Dim folderLookup As New Dictionary(Of String, System.Windows.Forms.TreeNode)(StringComparer.OrdinalIgnoreCase)
            For Each ep As Plugin.EndpointInfo In playlistEps
                Dim parent As System.Windows.Forms.TreeNode = playlistGroup
                If ep.FolderPath IsNot Nothing AndAlso ep.FolderPath.Length > 0 Then
                    Dim cumulative As String = ""
                    For Each seg As String In ep.FolderPath
                        cumulative = If(cumulative.Length = 0, seg, cumulative & "\" & seg)
                        Dim existing As System.Windows.Forms.TreeNode = Nothing
                        If folderLookup.TryGetValue(cumulative, existing) Then
                            parent = existing
                        Else
                            Dim fn As New System.Windows.Forms.TreeNode(seg) With {
                                .Tag = TagFolderGroup
                            }
                            parent.Nodes.Add(fn)
                            folderLookup(cumulative) = fn
                            parent = fn
                        End If
                    Next
                End If
                parent.Nodes.Add(BuildEndpointNode(ep))
            Next
            ' Compute parent states bottom-up from their children, then auto-expand the two
            ' top-level groups so the user lands on a useful default view.
            RecomputeAllParentStates()
            RecolourAllGroupNodes()
            filterGroup.Expand()
            playlistGroup.Expand()
            ' Initialise the Visible / Top level checkboxes (nothing checked yet → disabled).
            UpdateViewPropertyChecks()
            ' Grey out nodes that can't take the (just-selected) template's category.
            RefreshViewTreeAvailability()
        Finally
            viewsSuppressEvents = False
        End Try
    End Sub

    ' Build a single endpoint node with its path-children and initial state from its binding.
    ' Path-children carry Tag = Nothing so click handlers can ignore them.
    '
    ' IMPORTANT: the checkbox state and binding.Exposed are decoupled by design.
    '   StateImageIndex = transient selection for the next Apply (starts unchecked on load).
    '   binding.Exposed = the real hide/show state, persisted (Apply + Visibility checkbox).
    '   ForeColor       = visual reflection of binding.Exposed (grey = hidden).
    ' So a hidden endpoint shows as: greyed text + empty checkbox. Check it, tick Visibility
    ' (or apply a template - applying also re-exposes).
    Private Function BuildEndpointNode(ep As Plugin.EndpointInfo) As System.Windows.Forms.TreeNode
        Dim binding As Plugin.EndpointBinding = Plugin.View.GetBinding(ep.Id)
        Dim exposed As Boolean = If(binding Is Nothing, True, binding.Exposed)
        Dim epNode As New System.Windows.Forms.TreeNode(EndpointNodeText(ep)) With {
            .Tag = ep
        }
        epNode.StateImageIndex = StateUnchecked   ' fresh selection each load
        epNode.ForeColor = If(exposed, System.Drawing.SystemColors.WindowText, System.Drawing.SystemColors.GrayText)
        AddPathChildren(epNode, binding, exposed)
        Return epNode
    End Function

    ' Display label for an endpoint row: optional top-level pin marker + endpoint name + the
    ' name of the template the endpoint follows (unbound endpoints show no suffix).
    Private Function EndpointNodeText(ep As Plugin.EndpointInfo) As String
        Dim binding As Plugin.EndpointBinding = Plugin.View.GetBinding(ep.Id)
        Dim text As String = If(binding IsNot Nothing AndAlso binding.AtTopLevel, TopLevelMarker, "") & ep.DisplayName
        If binding IsNot Nothing AndAlso Not String.IsNullOrEmpty(binding.TemplateKey) Then
            Dim t As Plugin.BrowseTemplate = Plugin.View.BrowseTemplates.FirstOrDefault(Function(x) x.Key = binding.TemplateKey)
            If t IsNot Nothing Then text &= "  —  " & t.Name
        End If
        Return text
    End Function

    ' (Re)populate an endpoint node's informational path-children rows from its binding.
    ' Path-children follow their endpoint's visibility: black when the endpoint is exposed,
    ' grey only when it's hidden (so a visible node's paths read clearly).
    Private Sub AddPathChildren(epNode As System.Windows.Forms.TreeNode, binding As Plugin.EndpointBinding, exposed As Boolean)
        epNode.Nodes.Clear()
        If binding Is Nothing OrElse binding.Paths Is Nothing Then Return
        For Each p As Plugin.BrowsePath In binding.Paths
            Dim pathNode As New System.Windows.Forms.TreeNode(Plugin.View.FormatBrowsePathDisplay(p, True)) With {
                .Tag = Nothing,
                .ForeColor = If(exposed, System.Drawing.SystemColors.WindowText, System.Drawing.SystemColors.GrayText),
                .StateImageIndex = -1
            }
            epNode.Nodes.Add(pathNode)
        Next
    End Sub

    ' Reflect the current bindings into the endpoint tree in place: every endpoint row's label
    ' (template-name suffix), and - for endpoints following childrenForTemplateKey - its
    ' path-children rows. Nodes are updated, never recreated, so check states, expansion and
    ' scroll all survive. Pass Nothing to refresh labels only (rename / unbind).
    Private Sub RefreshEndpointNodesFromBindings(childrenForTemplateKey As String)
        Me.trvLibViwEndpoints.BeginUpdate()
        Try
            For Each n As System.Windows.Forms.TreeNode In CollectAllEndpointNodes(Me.trvLibViwEndpoints.Nodes)
                Dim info As Plugin.EndpointInfo = TryCast(n.Tag, Plugin.EndpointInfo)
                If info Is Nothing Then Continue For
                n.Text = EndpointNodeText(info)
                If Not String.IsNullOrEmpty(childrenForTemplateKey) Then
                    Dim binding As Plugin.EndpointBinding = Plugin.View.GetBinding(info.Id)
                    If binding IsNot Nothing AndAlso binding.TemplateKey = childrenForTemplateKey Then
                        Dim wasExpanded As Boolean = n.IsExpanded
                        AddPathChildren(n, binding, binding.Exposed)
                        If wasExpanded Then n.Expand()
                    End If
                End If
            Next
        Finally
            Me.trvLibViwEndpoints.EndUpdate()
        End Try
    End Sub

    ' Walk all top-level group nodes and recompute their state from their descendants.
    ' Endpoint nodes are already set by BuildEndpointNode; parent groups derive from leaves.
    Private Sub RecomputeAllParentStates()
        For Each top As System.Windows.Forms.TreeNode In Me.trvLibViwEndpoints.Nodes
            RecomputeNodeState(top)
        Next
    End Sub

    ' Recolour every group/folder node from the visibility of its endpoint descendants: a group
    ' greys out only once EVERY endpoint under it is hidden, and shows in normal text while any
    ' remains visible. Endpoint + path-child colours are owned by BuildEndpointNode /
    ' ApplyViewProperty (grey = binding.Exposed False); this pass only touches the aggregating
    ' group rows so that hiding a whole folder greys the folder labels too, not just the leaves.
    Private Sub RecolourAllGroupNodes()
        For Each top As System.Windows.Forms.TreeNode In Me.trvLibViwEndpoints.Nodes
            Dim visible As Integer = 0, total As Integer = 0
            RecolourGroupNode(top, visible, total)
        Next
    End Sub

    ' Accumulates this subtree's (visible, total) endpoint counts into the ByRef params and,
    ' for group/folder nodes, sets ForeColor grey when the subtree has endpoints but none are
    ' visible. Endpoint leaves only contribute to the counts (their colour is set elsewhere);
    ' path-children (Tag = Nothing) are skipped entirely.
    Private Sub RecolourGroupNode(node As System.Windows.Forms.TreeNode, ByRef visible As Integer, ByRef total As Integer)
        If TypeOf node.Tag Is Plugin.EndpointInfo Then
            Dim b As Plugin.EndpointBinding = Plugin.View.GetBinding(DirectCast(node.Tag, Plugin.EndpointInfo).Id)
            total += 1
            If b Is Nothing OrElse b.Exposed Then visible += 1
            Return
        End If
        If node.Tag Is Nothing Then Return  ' path-child - not aggregated, colour owned by its endpoint
        Dim childVisible As Integer = 0, childTotal As Integer = 0
        For Each child As System.Windows.Forms.TreeNode In node.Nodes
            RecolourGroupNode(child, childVisible, childTotal)
        Next
        node.ForeColor = If(childTotal > 0 AndAlso childVisible = 0, System.Drawing.SystemColors.GrayText, System.Drawing.SystemColors.WindowText)
        visible += childVisible
        total += childTotal
    End Sub

    ' Recursively recompute state for a node from its descendants. Returns the resolved state.
    ' Endpoint nodes are leaves - their state was set by BuildEndpointNode and we don't touch
    ' it. Group/folder nodes aggregate: all-checked → checked, all-unchecked → unchecked,
    ' anything in between → mixed. Path-children (Tag = Nothing) are ignored.
    Private Function RecomputeNodeState(node As System.Windows.Forms.TreeNode) As Integer
        If TypeOf node.Tag Is Plugin.EndpointInfo Then
            Return node.StateImageIndex
        End If
        If node.Tag Is Nothing Then
            ' Not a checkable node (path-child) - don't influence parent state.
            Return -1
        End If
        ' Group / folder node - aggregate over checkable descendants.
        Dim anyChecked As Boolean = False
        Dim anyUnchecked As Boolean = False
        For Each child As System.Windows.Forms.TreeNode In node.Nodes
            Dim s As Integer = RecomputeNodeState(child)
            Select Case s
                Case StateChecked : anyChecked = True
                Case StateUnchecked : anyUnchecked = True
                Case StateMixed
                    anyChecked = True
                    anyUnchecked = True
            End Select
        Next
        Dim result As Integer
        If anyChecked AndAlso anyUnchecked Then
            result = StateMixed
        ElseIf anyChecked Then
            result = StateChecked
        Else
            result = StateUnchecked
        End If
        node.StateImageIndex = result
        Return result
    End Function


    ' Walk up from a node, recomputing each ancestor's state. Stops at the root collection.
    Private Sub RecomputeAncestors(node As System.Windows.Forms.TreeNode)
        Dim p As System.Windows.Forms.TreeNode = node.Parent
        While p IsNot Nothing
            RecomputeNodeStateFromDirectChildren(p)
            p = p.Parent
        End While
    End Sub

    ' Single-level recompute (non-recursive) - used after we've already set child states
    ' explicitly via cascade, so we just need to roll up one level at a time.
    Private Sub RecomputeNodeStateFromDirectChildren(node As System.Windows.Forms.TreeNode)
        If node.Tag Is Nothing OrElse TypeOf node.Tag Is Plugin.EndpointInfo Then Return
        Dim anyChecked As Boolean = False
        Dim anyUnchecked As Boolean = False
        For Each child As System.Windows.Forms.TreeNode In node.Nodes
            If child.Tag Is Nothing Then Continue For
            Select Case child.StateImageIndex
                Case StateChecked : anyChecked = True
                Case StateUnchecked : anyUnchecked = True
                Case StateMixed
                    anyChecked = True
                    anyUnchecked = True
            End Select
        Next
        If anyChecked AndAlso anyUnchecked Then
            node.StateImageIndex = StateMixed
        ElseIf anyChecked Then
            node.StateImageIndex = StateChecked
        Else
            node.StateImageIndex = StateUnchecked
        End If
    End Sub

    ' Reload the template dropdown from Plugin.View.BrowseTemplates. Optionally select a named one;
    ' if Nothing, selects the first item if any.
    Private Sub RefreshTemplateCombo(selectName As String)
        viewsSuppressEvents = True
        Try
            ' Keep templates grouped by category so the owner-drawn category bands render as
            ' three contiguous groups (Standard, Radio, Podcast). Items stay 1:1 with
            ' BrowseTemplates - the bands are drawn, not separate list items.
            SortTemplatesByCategory()
            ' Single shared Templates list (lives on the outer UPnP tab, visible from
            ' both the Paths and View sub-tabs). Drives both the editor and the apply
            ' target - no separate viewsApplyTemplateList anymore.
            Me.lstLibPthTemplate.Items.Clear()
            For Each t As Plugin.BrowseTemplate In Plugin.View.BrowseTemplates
                Me.lstLibPthTemplate.Items.Add(t)
            Next
            If Me.lstLibPthTemplate.Items.Count > 0 Then
                Dim idx As Integer = 0
                If selectName IsNot Nothing Then
                    For i As Integer = 0 To Me.lstLibPthTemplate.Items.Count - 1
                        If DirectCast(Me.lstLibPthTemplate.Items(i), Plugin.BrowseTemplate).Name = selectName Then
                            idx = i
                            Exit For
                        End If
                    Next
                End If
                Me.lstLibPthTemplate.SelectedIndex = idx
            End If
        Finally
            viewsSuppressEvents = False
        End Try
        ' Trigger the load explicitly since we suppressed it above.
        lstLibPthTemplate_SelectedIndexChanged(Nothing, Nothing)
    End Sub

    ' Load the currently-selected template into the editing area. Edits are applied straight to
    ' the live template object (no Save step) - the user's changes persist as they type/click;
    ' the dialog's own Save button writes the whole templates list to disk on close.
    Private Sub lstLibPthTemplate_SelectedIndexChanged(sender As Object, e As EventArgs)
        If viewsSuppressEvents Then Return
        Dim src As Plugin.BrowseTemplate = TryCast(Me.lstLibPthTemplate.SelectedItem, Plugin.BrowseTemplate)
        If src Is Nothing Then
            viewsEditingTemplate = Nothing
            viewsSuppressEvents = True
            Try
                Me.txtLibPthName.Text = ""
                Me.lblLibPthNameCollision.Visible = False
            Finally
                viewsSuppressEvents = False
            End Try
        Else
            viewsEditingTemplate = src
            viewsSuppressEvents = True
            Try
                Me.txtLibPthName.Text = src.Name
                Me.lblLibPthNameCollision.Visible = False
            Finally
                viewsSuppressEvents = False
            End Try
        End If
        ApplyEditingTemplateCategory()
        RefreshViewTreeAvailability()
        RefreshPathsList()
        UpdateTemplateDeleteButtonState()
        UpdateViewApplyButtonState()
        ' Active template filter follows the selection: re-filter the tree for the newly
        ' selected template. Tree-only, so no visible churn outside the View sub-tab.
        If viewsFilterActive Then RebuildFilteredTree()
    End Sub

    ' Push the edited template's data-access category onto every field picker so their menus
    ' mask to that category's allowed fields (Radio/Podcast restrict; Standard shows all).
    ' Standard when no template is selected.
    Private Sub ApplyEditingTemplateCategory()
        Dim cat As Plugin.PathCategory = If(viewsEditingTemplate IsNot Nothing, viewsEditingTemplate.Category, Plugin.PathCategory.Standard)
        Me.fpcLibPthField1.FieldCategory = cat
        Me.fpcLibPthField2.FieldCategory = cat
        Me.fpcLibPthField3.FieldCategory = cat
        Me.fpcLibPthAlbum1.FieldCategory = cat
        Me.fpcLibPthAlbum2.FieldCategory = cat
    End Sub

    ' Refresh the path list within the editing template; reload the selected path's editor.
    ' Display labels are auto-generated from each path's Hierarchy + Leaf via
    ' Plugin.View.FormatBrowsePathDisplay - no user-typed name to fall back to.
    Private Sub RefreshPathsList()
        viewsSuppressEvents = True
        Try
            Me.lstLibPthList.Items.Clear()
            If viewsEditingTemplate IsNot Nothing Then
                For Each p As Plugin.BrowsePath In viewsEditingTemplate.Paths
                    Me.lstLibPthList.Items.Add(Plugin.View.FormatBrowsePathDisplay(p, True))
                Next
                If Me.lstLibPthList.Items.Count > 0 Then
                    Me.lstLibPthList.SelectedIndex = 0
                End If
            End If
        Finally
            viewsSuppressEvents = False
        End Try
        lstLibPthList_SelectedIndexChanged(Nothing, Nothing)
    End Sub

    ' Load the selected path into the editor controls.
    Private Sub lstLibPthList_SelectedIndexChanged(sender As Object, e As EventArgs)
        If viewsSuppressEvents Then Return
        viewsSuppressEvents = True
        Try
            Dim p As Plugin.BrowsePath = GetSelectedPath()
            If p Is Nothing Then
                Me.fpcLibPthField1.SetSelectedField("")
                Me.fpcLibPthField2.SetSelectedField("")
                Me.fpcLibPthField3.SetSelectedField("")
                Me.chkLibPthField1Bucket.Checked = False
                Me.chkLibPthField2Bucket.Checked = False
                Me.chkLibPthField3Bucket.Checked = False
                SetSortButton(Me.btnLibPthField1Sort, False)
                SetSortButton(Me.btnLibPthField2Sort, False)
                SetSortButton(Me.btnLibPthField3Sort, False)
                Me.radLibPthLeafAT.Checked = True
                Me.chkLibPthIncludeAllTracks.Checked = False
                LoadAlbumGroupBy(Nothing)
                SetPathEditorEnabled(False)
            Else
                Dim e0 As Plugin.HierarchyEntry = GetHierarchySafe(p, 0)
                Dim e1 As Plugin.HierarchyEntry = GetHierarchySafe(p, 1)
                Dim e2 As Plugin.HierarchyEntry = GetHierarchySafe(p, 2)
                Me.fpcLibPthField1.SetSelectedField(If(e0 Is Nothing, "", e0.Field))
                Me.fpcLibPthField2.SetSelectedField(If(e1 Is Nothing, "", e1.Field))
                Me.fpcLibPthField3.SetSelectedField(If(e2 Is Nothing, "", e2.Field))
                Me.chkLibPthField1Bucket.Checked = (e0 IsNot Nothing AndAlso e0.BucketByLetter)
                Me.chkLibPthField2Bucket.Checked = (e1 IsNot Nothing AndAlso e1.BucketByLetter)
                Me.chkLibPthField3Bucket.Checked = (e2 IsNot Nothing AndAlso e2.BucketByLetter)
                SetSortButton(Me.btnLibPthField1Sort, e0 IsNot Nothing AndAlso e0.SortDescending)
                SetSortButton(Me.btnLibPthField2Sort, e1 IsNot Nothing AndAlso e1.SortDescending)
                SetSortButton(Me.btnLibPthField3Sort, e2 IsNot Nothing AndAlso e2.SortDescending)
                Me.radLibPthLeafAT.Checked = (p.Leaf = Plugin.LeafMode.AT)
                Me.radLibPthLeafT.Checked = (p.Leaf = Plugin.LeafMode.T)
                Me.chkLibPthIncludeAllTracks.Checked = p.IncludeAllTracks
                LoadAlbumGroupBy(p.AlbumGroupBy)
                SetPathEditorEnabled(True)
                RefreshBucketAvailability()
            End If
            RefreshLeafOptionsVisibility()
        Finally
            viewsSuppressEvents = False
        End Try
    End Sub

    Private Function GetSelectedPath() As Plugin.BrowsePath
        If viewsEditingTemplate Is Nothing Then Return Nothing
        Dim idx As Integer = Me.lstLibPthList.SelectedIndex
        If idx < 0 OrElse idx >= viewsEditingTemplate.Paths.Length Then Return Nothing
        Return viewsEditingTemplate.Paths(idx)
    End Function

    Private Function GetHierarchySafe(p As Plugin.BrowsePath, index As Integer) As Plugin.HierarchyEntry
        If p Is Nothing OrElse p.Hierarchy Is Nothing OrElse index >= p.Hierarchy.Length Then Return Nothing
        Return p.Hierarchy(index)
    End Function

    Private Sub SetPathEditorEnabled(enabled As Boolean)
        Me.fpcLibPthField1.Enabled = enabled
        Me.fpcLibPthField2.Enabled = enabled
        Me.fpcLibPthField3.Enabled = enabled
        Me.radLibPthLeafAT.Enabled = enabled
        Me.radLibPthLeafT.Enabled = enabled
        Me.fpcLibPthAlbum1.Enabled = enabled
        Me.fpcLibPthAlbum2.Enabled = enabled
        Me.btnLibPthAlbum1Sort.Enabled = enabled
        Me.btnLibPthAlbum2Sort.Enabled = enabled
    End Sub

    ' Read the 2 album group-by rows into an AlbumGroupField array (empty rows skipped).
    ' This list defines the AT-leaf album identity + title + sort (see Plugin.AlbumGroupField).
    Private Function ReadAlbumGroupBy() As Plugin.AlbumGroupField()
        Dim pickers() As Plugin.FieldPickerCombo = New Plugin.FieldPickerCombo() {Me.fpcLibPthAlbum1, Me.fpcLibPthAlbum2}
        Dim sortBtns() As System.Windows.Forms.Button = New System.Windows.Forms.Button() {Me.btnLibPthAlbum1Sort, Me.btnLibPthAlbum2Sort}
        Dim entries As New List(Of Plugin.AlbumGroupField)
        For i As Integer = 0 To pickers.Length - 1
            Dim name As String = pickers(i).SelectedField
            If Not String.IsNullOrEmpty(name) Then
                entries.Add(New Plugin.AlbumGroupField(name, IsSortDescending(sortBtns(i))))
            End If
        Next
        Return entries.ToArray()
    End Function

    ' Load an AlbumGroupField array into the 2 album group-by rows; unused rows are cleared.
    Private Sub LoadAlbumGroupBy(spec As Plugin.AlbumGroupField())
        Dim pickers() As Plugin.FieldPickerCombo = New Plugin.FieldPickerCombo() {Me.fpcLibPthAlbum1, Me.fpcLibPthAlbum2}
        Dim sortBtns() As System.Windows.Forms.Button = New System.Windows.Forms.Button() {Me.btnLibPthAlbum1Sort, Me.btnLibPthAlbum2Sort}
        For i As Integer = 0 To pickers.Length - 1
            If spec IsNot Nothing AndAlso i < spec.Length AndAlso spec(i) IsNot Nothing Then
                pickers(i).SetSelectedField(spec(i).Field)
                SetSortButton(sortBtns(i), spec(i).SortDescending)
            Else
                pickers(i).SetSelectedField("")
                SetSortButton(sortBtns(i), False)
            End If
        Next
    End Sub

    ' User-edit handlers - write back into the editing template (NOT into Settings yet; Save does that).
    ' Field/leaf edits both refresh the auto-generated display label in the path list, since
    ' the label is derived from Hierarchy + Leaf via Plugin.View.FormatBrowsePathDisplay.
    ' Rebuild the editing path's Hierarchy + leaf settings from the three field dropdowns, their
    ' three bucket checkboxes, the AT/T radio, IncludeAllTracks, and the album group-by rows.
    ' Triggered by any of those control's change events.
    Private Sub viewsPathEditor_Changed(sender As Object, e As EventArgs)
        If viewsSuppressEvents Then Return
        Dim p As Plugin.BrowsePath = GetSelectedPath()
        If p Is Nothing Then Return
        Dim entries As New List(Of Plugin.HierarchyEntry)
        Dim pickers() As Plugin.FieldPickerCombo = New Plugin.FieldPickerCombo() {Me.fpcLibPthField1, Me.fpcLibPthField2, Me.fpcLibPthField3}
        Dim buckets() As System.Windows.Forms.CheckBox = New System.Windows.Forms.CheckBox() {Me.chkLibPthField1Bucket, Me.chkLibPthField2Bucket, Me.chkLibPthField3Bucket}
        Dim sortBtns() As System.Windows.Forms.Button = New System.Windows.Forms.Button() {Me.btnLibPthField1Sort, Me.btnLibPthField2Sort, Me.btnLibPthField3Sort}
        For i As Integer = 0 To pickers.Length - 1
            Dim name As String = pickers(i).SelectedField
            If Not String.IsNullOrEmpty(name) Then
                entries.Add(New Plugin.HierarchyEntry(name, buckets(i).Checked, IsSortDescending(sortBtns(i))))
            End If
        Next
        p.Hierarchy = entries.ToArray()
        p.Leaf = If(Me.radLibPthLeafAT.Checked, Plugin.LeafMode.AT, Plugin.LeafMode.T)
        p.IncludeAllTracks = Me.chkLibPthIncludeAllTracks.Checked
        p.AlbumGroupBy = ReadAlbumGroupBy()
        RefreshBucketAvailability()
        RefreshSelectedPathLabel(p)
        PropagateTemplateEdit()
    End Sub

    ' Reference model: a template edit immediately re-stamps every endpoint bound to the
    ' editing template, and the Views tree mirrors the new paths in place.
    Private Sub PropagateTemplateEdit()
        If viewsEditingTemplate Is Nothing Then Return
        Plugin.View.SyncBindingsFromTemplates()
        RefreshEndpointNodesFromBindings(viewsEditingTemplate.Key)
    End Sub

    ' ↑/↓ click handler - single sub shared by all three sort buttons. Toggles the sender's
    ' text and triggers a full path-editor refresh so the data model + label update.
    Private Sub viewsFieldSort_Click(sender As Object, e As EventArgs)
        Dim btn As System.Windows.Forms.Button = DirectCast(sender, System.Windows.Forms.Button)
        SetSortButton(btn, Not IsSortDescending(btn))
        viewsPathEditor_Changed(sender, e)
    End Sub

    ' The three field-sort buttons are icon-only; their direction state lives in .Tag
    ' (True = descending Z→A, False/Nothing = ascending A→Z) and the icon mirrors it.
    Private sortIconAsc As System.Drawing.Image
    Private sortIconDesc As System.Drawing.Image

    Private Sub SetSortButton(btn As System.Windows.Forms.Button, descending As Boolean)
        btn.Tag = descending
        btn.Image = If(descending, sortIconDesc, sortIconAsc)
    End Sub

    Private Function IsSortDescending(btn As System.Windows.Forms.Button) As Boolean
        Return btn.Tag IsNot Nothing AndAlso CBool(btn.Tag)
    End Function

    ' Walks the 3 slots: when the field combo is (none), disable the bucket + sort buttons
    ' (meaningless without a field) and clear their state. When the field IS selected, refresh
    ' the bucket tooltip with the field's display name ("Split [Album Artist] by their first
    ' letter") so the user knows what'll happen.
    Private Sub RefreshBucketAvailability()
        Dim pickers() As Plugin.FieldPickerCombo = New Plugin.FieldPickerCombo() {Me.fpcLibPthField1, Me.fpcLibPthField2, Me.fpcLibPthField3}
        Dim buckets() As System.Windows.Forms.CheckBox = New System.Windows.Forms.CheckBox() {Me.chkLibPthField1Bucket, Me.chkLibPthField2Bucket, Me.chkLibPthField3Bucket}
        Dim sortBtns() As System.Windows.Forms.Button = New System.Windows.Forms.Button() {Me.btnLibPthField1Sort, Me.btnLibPthField2Sort, Me.btnLibPthField3Sort}
        For i As Integer = 0 To pickers.Length - 1
            Dim name As String = pickers(i).SelectedField
            Dim hasField As Boolean = Not String.IsNullOrEmpty(name)
            buckets(i).Enabled = hasField
            sortBtns(i).Enabled = hasField
            If Not hasField Then
                ' Reset state silently when no field - bucket off, sort Asc.
                viewsSuppressEvents = True
                Try
                    buckets(i).Checked = False
                    SetSortButton(sortBtns(i), False)
                Finally
                    viewsSuppressEvents = False
                End Try
                viewsBucketTip.SetToolTip(buckets(i), Plugin.L("ViewsBucketByLetterTip"))
            Else
                Dim displayName As String = Plugin.View.FieldDisplayName(name)
                viewsBucketTip.SetToolTip(buckets(i), String.Format(Plugin.L("ViewsBucketByLetterTipDynamic"), displayName))
            End If
        Next
    End Sub

    ' Legacy handlers kept as thin wrappers so existing AddHandler wiring still resolves.
    Private Sub viewsField_SelectedIndexChanged(sender As Object, e As EventArgs)
        viewsPathEditor_Changed(sender, e)
    End Sub

    Private Sub viewsLeaf_CheckedChanged(sender As Object, e As EventArgs)
        RefreshLeafOptionsVisibility()
        viewsPathEditor_Changed(sender, e)
    End Sub

    ' The three album-tracks options (include [All Tracks], prefix album with artist,
    ' sort albums by year) only apply to the Album → Tracks leaf mode; hide them
    ' entirely when Flat Tracks is the selected leaf.
    Private Sub RefreshLeafOptionsVisibility()
        Dim atSelected As Boolean = Me.radLibPthLeafAT.Checked
        Me.chkLibPthIncludeAllTracks.Visible = atSelected
        Me.lblLibPthAlbumGroup.Visible = atSelected
        Me.fpcLibPthAlbum1.Visible = atSelected
        Me.fpcLibPthAlbum2.Visible = atSelected
        Me.btnLibPthAlbum1Sort.Visible = atSelected
        Me.btnLibPthAlbum2Sort.Visible = atSelected
    End Sub

    Private Sub RefreshSelectedPathLabel(p As Plugin.BrowsePath)
        viewsSuppressEvents = True
        Try
            Me.lstLibPthList.Items(Me.lstLibPthList.SelectedIndex) = Plugin.View.FormatBrowsePathDisplay(p, True)
        Finally
            viewsSuppressEvents = False
        End Try
    End Sub

    ' Template buttons.
    ' Create a new path set immediately - no popup. Name starts as "New", "New (2)", ...
    ' auto-uniquified so the create button always succeeds. User then edits the Name field
    ' in the editor to rename.
    Private Sub btnLibPthTemplateNew_Click(sender As Object, e As EventArgs)
        Dim baseName As String = Plugin.L("ViewsNewTemplateName")  ' "New"
        Dim candidateName As String = baseName
        Dim n As Integer = 2
        While Plugin.View.BrowseTemplates.Any(Function(tmpl) String.Equals(tmpl.Name, candidateName, StringComparison.OrdinalIgnoreCase))
            candidateName = baseName & " (" & n.ToString() & ")"
            n += 1
        End While
        ' New templates are always Standard: the Reserved band is a closed set (Radio +
        ' Podcasts, permanently paired with their nodes) - a user-created template in those
        ' categories could never be applied to anything.
        Dim t As New Plugin.BrowseTemplate With {
            .Key = Guid.NewGuid().ToString("N"),
            .Name = candidateName,
            .Category = Plugin.PathCategory.Standard,
            .Paths = New Plugin.BrowsePath() {
                New Plugin.BrowsePath With {
                    .Hierarchy = New Plugin.HierarchyEntry() {},
                    .Leaf = Plugin.LeafMode.AT
                }
            }
        }
        Plugin.View.BrowseTemplates.Add(t)
        RefreshTemplateCombo(candidateName)
    End Sub

    ' Vertical padding above+below the text in each owner-drawn template-list row. The category
    ' band header and the template rows use the SAME row height (Font.Height + 2×pad) with
    ' vertically-centred text, so spacing reads uniform above and below every label.
    Private Const TemplateRowVPad As Integer = 3

    ' Height of one row (band header or template) in the owner-drawn template list.
    Private ReadOnly Property TemplateRowHeight As Integer
        Get
            Return Me.lstLibPthTemplate.Font.Height + TemplateRowVPad * 2
        End Get
    End Property

    ' Stable-group BrowseTemplates by category (Standard, then Radio, then Podcast). OrderBy is
    ' a stable sort so each template keeps its relative position within its category.
    Private Shared Sub SortTemplatesByCategory()
        Dim ordered As List(Of Plugin.BrowseTemplate) = Plugin.View.BrowseTemplates.OrderBy(Function(t) CInt(t.Category)).ToList()
        Plugin.View.BrowseTemplates.Clear()
        Plugin.View.BrowseTemplates.AddRange(ordered)
    End Sub

    ' The list draws TWO bands, not one per category: "Standard", then "Reserved" covering
    ' both non-Standard categories (Radio + Podcast). Purely a display grouping - the
    ' categories stay distinct underneath (field masking, apply-gating, new-template
    ' inheritance all still key on PathCategory).
    Private Shared Function BandOf(cat As Plugin.PathCategory) As Boolean
        Return cat <> Plugin.PathCategory.Standard  ' False = Standard band, True = Reserved band
    End Function

    ' Localized band labels for the template list.
    Private Shared Function CategoryDisplayName(cat As Plugin.PathCategory) As String
        Return If(BandOf(cat), Plugin.L("ViewsCategoryReserved"), Plugin.L("ViewsCategoryStandard"))
    End Function

    ' True when the item at index is the first of its band - i.e. a band header should be
    ' drawn above it (index 0, or the previous item belongs to the other band).
    Private Function IsFirstOfCategory(index As Integer) As Boolean
        If index < 0 OrElse index >= Me.lstLibPthTemplate.Items.Count Then Return False
        Dim cur As Plugin.BrowseTemplate = TryCast(Me.lstLibPthTemplate.Items(index), Plugin.BrowseTemplate)
        If cur Is Nothing Then Return False
        If index = 0 Then Return True
        Dim prev As Plugin.BrowseTemplate = TryCast(Me.lstLibPthTemplate.Items(index - 1), Plugin.BrowseTemplate)
        Return prev Is Nothing OrElse BandOf(prev.Category) <> BandOf(cur.Category)
    End Function

    Private Sub lstLibPthTemplate_MeasureItem(sender As Object, e As System.Windows.Forms.MeasureItemEventArgs)
        ' One row for the template; a second identical row stacked on top when this is the first
        ' template of its category (the band header). Same height keeps the list rhythm uniform.
        e.ItemHeight = If(IsFirstOfCategory(e.Index), TemplateRowHeight * 2, TemplateRowHeight)
    End Sub

    Private Sub lstLibPthTemplate_DrawItem(sender As Object, e As System.Windows.Forms.DrawItemEventArgs)
        If e.Index < 0 OrElse e.Index >= Me.lstLibPthTemplate.Items.Count Then Return
        Dim t As Plugin.BrowseTemplate = TryCast(Me.lstLibPthTemplate.Items(e.Index), Plugin.BrowseTemplate)
        If t Is Nothing Then Return
        Const textFlags As System.Windows.Forms.TextFormatFlags =
            System.Windows.Forms.TextFormatFlags.Left Or System.Windows.Forms.TextFormatFlags.VerticalCenter
        Dim g As System.Drawing.Graphics = e.Graphics
        Dim rowH As Integer = TemplateRowHeight
        Dim top As Integer = e.Bounds.Top
        If IsFirstOfCategory(e.Index) Then
            ' Band header row - same height as a template row, label vertically centred.
            Dim bandRect As New System.Drawing.Rectangle(e.Bounds.Left, top, e.Bounds.Width, rowH)
            Using br As New System.Drawing.SolidBrush(System.Drawing.SystemColors.ControlLight)
                g.FillRectangle(br, bandRect)
            End Using
            Using f As New System.Drawing.Font(Me.lstLibPthTemplate.Font, System.Drawing.FontStyle.Bold)
                System.Windows.Forms.TextRenderer.DrawText(g, CategoryDisplayName(t.Category), f, New System.Drawing.Rectangle(bandRect.Left + 6, bandRect.Top, bandRect.Width - 6, bandRect.Height), System.Drawing.SystemColors.GrayText, textFlags)
            End Using
            top += rowH
        End If
        ' Template row. The "(n)" suffix is the live count of nodes following the template -
        ' hidden at zero (an unused template just shows its name, and Delete being enabled
        ' already says "unused").
        Dim itemRect As New System.Drawing.Rectangle(e.Bounds.Left, top, e.Bounds.Width, rowH)
        Dim selected As Boolean = (e.State And System.Windows.Forms.DrawItemState.Selected) <> 0
        Using bg As New System.Drawing.SolidBrush(If(selected, System.Drawing.SystemColors.Highlight, Me.lstLibPthTemplate.BackColor))
            g.FillRectangle(bg, itemRect)
        End Using
        Dim fg As System.Drawing.Color = If(selected, System.Drawing.SystemColors.HighlightText, Me.lstLibPthTemplate.ForeColor)
        Dim usage As Integer = TemplateUsageCount(t.Key)
        Dim rowText As String = If(usage > 0, t.Name & " (" & usage.ToString() & ")", t.Name)
        System.Windows.Forms.TextRenderer.DrawText(g, rowText, Me.lstLibPthTemplate.Font, New System.Drawing.Rectangle(itemRect.Left + 6, itemRect.Top, itemRect.Width - 6, itemRect.Height), fg, textFlags)
    End Sub

    ' Rename happens inline now - user edits the Name textbox; live collision check shows
    ' a red label if another template shares the name. Save refuses on collision.
    Private Sub pathsName_TextChanged(sender As Object, e As EventArgs)
        If viewsSuppressEvents Then Return
        If viewsEditingTemplate Is Nothing Then Return
        Dim newName As String = Me.txtLibPthName.Text
        Dim collides As Boolean = False
        For Each t As Plugin.BrowseTemplate In Plugin.View.BrowseTemplates
            If t IsNot viewsEditingTemplate AndAlso String.Equals(t.Name, newName, StringComparison.OrdinalIgnoreCase) Then
                collides = True
                Exit For
            End If
        Next
        Me.lblLibPthNameCollision.Visible = collides
        If Not collides Then
            viewsEditingTemplate.Name = newName
            ' Update the sidebar list entry for live feedback.
            viewsSuppressEvents = True
            Try
                Dim idx As Integer = Me.lstLibPthTemplate.SelectedIndex
                If idx >= 0 Then Me.lstLibPthTemplate.Items(idx) = viewsEditingTemplate
            Finally
                viewsSuppressEvents = False
            End Try
            ' Identity is the Key, so a rename costs nothing - just mirror the new name into
            ' the endpoint rows that display it.
            RefreshEndpointNodesFromBindings(Nothing)
        End If
    End Sub

    ' Delete is gated on use: the button is disabled while any node follows the template
    ' (UpdateTemplateDeleteButtonState), so no follower can ever be orphaned by a delete.
    ' That gate is also what protects the Radio/Podcasts category anchors - their nodes
    ' always follow a template of their category, so the last one is never deletable.
    Private Sub btnLibPthTemplateDelete_Click(sender As Object, e As EventArgs)
        If viewsEditingTemplate Is Nothing Then Return
        Dim key As String = viewsEditingTemplate.Key
        ' Defensive re-check - never trust the button state alone.
        If TemplateUsageCount(key) > 0 Then Return
        Dim result As DialogResult = MessageBox.Show(Me, String.Format(Plugin.L("ViewsConfirmDeleteTemplate"), viewsEditingTemplate.Name), Plugin.L("MessageBoxTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Question)
        If result <> DialogResult.Yes Then Return
        For i As Integer = Plugin.View.BrowseTemplates.Count - 1 To 0 Step -1
            If Plugin.View.BrowseTemplates(i).Key = key Then Plugin.View.BrowseTemplates.RemoveAt(i)
        Next
        RefreshTemplateCombo(Nothing)
    End Sub

    ' Number of endpoints currently following a template. Drives the Delete gate and the
    ' usage count drawn behind each template row.
    Private Function TemplateUsageCount(key As String) As Integer
        If String.IsNullOrEmpty(key) Then Return 0
        Return Plugin.View.EndpointBindings.Where(Function(b) b.TemplateKey = key).Count()
    End Function

    ' Enable Delete only when the selected template is followed by no node. The row's own
    ' "(n)" usage suffix explains a disabled button at a glance.
    Private Sub UpdateTemplateDeleteButtonState()
        Me.btnLibPthTemplateDelete.Enabled =
            viewsEditingTemplate IsNot Nothing AndAlso TemplateUsageCount(viewsEditingTemplate.Key) = 0
    End Sub

    ' Apply is only meaningful for Standard templates: the Radio and Podcasts nodes are
    ' permanently paired with their category's template - reshaping it on the Paths tab
    ' propagates by itself, so with a Reserved selection there is nothing to apply.
    Private Sub UpdateViewApplyButtonState()
        Me.btnLibViwApply.Enabled =
            viewsEditingTemplate IsNot Nothing AndAlso viewsEditingTemplate.Category = Plugin.PathCategory.Standard
    End Sub

    ' Reorder the selected template up/down in the shared BrowseTemplates list. That order is
    ' what both the Paths picker and the View apply target display, so this is purely how the
    ' user wants their templates listed. No-op past either end; the moved template stays
    ' selected so ↑/↓ can be pressed repeatedly to walk it to its target position.
    Private Sub btnLibPthTemplateUp_Click(sender As Object, e As EventArgs)
        MoveSelectedTemplate(-1)
    End Sub

    Private Sub btnLibPthTemplateDown_Click(sender As Object, e As EventArgs)
        MoveSelectedTemplate(1)
    End Sub

    Private Sub MoveSelectedTemplate(delta As Integer)
        Dim moved As Plugin.BrowseTemplate = TryCast(Me.lstLibPthTemplate.SelectedItem, Plugin.BrowseTemplate)
        If moved Is Nothing Then Return
        Dim idx As Integer = Plugin.View.BrowseTemplates.IndexOf(moved)
        Dim target As Integer = idx + delta
        If idx < 0 OrElse target < 0 OrElse target >= Plugin.View.BrowseTemplates.Count Then Return
        ' Only reorder within the same category - categories stay contiguous and grouped, so a
        ' move that would cross a band boundary is a no-op (use it to walk within the section).
        If Plugin.View.BrowseTemplates(target).Category <> moved.Category Then Return
        Plugin.View.BrowseTemplates.RemoveAt(idx)
        Plugin.View.BrowseTemplates.Insert(target, moved)
        RefreshTemplateCombo(moved.Name)
    End Sub

    ' Path Add/Remove.
    Private Sub btnLibPthAdd_Click(sender As Object, e As EventArgs)
        If viewsEditingTemplate Is Nothing Then Return
        Dim newPath As New Plugin.BrowsePath With {
            .Hierarchy = New Plugin.HierarchyEntry() {},
            .Leaf = Plugin.LeafMode.AT
        }
        Dim list As New List(Of Plugin.BrowsePath)(viewsEditingTemplate.Paths)
        list.Add(newPath)
        viewsEditingTemplate.Paths = list.ToArray()
        RefreshPathsList()
        Me.lstLibPthList.SelectedIndex = Me.lstLibPthList.Items.Count - 1
        PropagateTemplateEdit()
    End Sub

    Private Sub btnLibPthRemove_Click(sender As Object, e As EventArgs)
        If viewsEditingTemplate Is Nothing Then Return
        Dim idx As Integer = Me.lstLibPthList.SelectedIndex
        If idx < 0 OrElse idx >= viewsEditingTemplate.Paths.Length Then Return
        Dim list As New List(Of Plugin.BrowsePath)(viewsEditingTemplate.Paths)
        list.RemoveAt(idx)
        viewsEditingTemplate.Paths = list.ToArray()
        RefreshPathsList()
        PropagateTemplateEdit()
    End Sub

    ' Reorder the selected path up/down in the editing template. No-op if the move would go
    ' past either end. After the swap, re-select the same path at its new index so the user
    ' can hit ↑/↓ repeatedly to walk a path to its target position.
    Private Sub btnLibPthUp_Click(sender As Object, e As EventArgs)
        MoveSelectedPath(-1)
    End Sub

    Private Sub btnLibPthDown_Click(sender As Object, e As EventArgs)
        MoveSelectedPath(1)
    End Sub

    Private Sub MoveSelectedPath(delta As Integer)
        If viewsEditingTemplate Is Nothing Then Return
        Dim idx As Integer = Me.lstLibPthList.SelectedIndex
        Dim target As Integer = idx + delta
        If idx < 0 OrElse target < 0 OrElse target >= viewsEditingTemplate.Paths.Length Then Return
        Dim list As New List(Of Plugin.BrowsePath)(viewsEditingTemplate.Paths)
        Dim moved As Plugin.BrowsePath = list(idx)
        list.RemoveAt(idx)
        list.Insert(target, moved)
        viewsEditingTemplate.Paths = list.ToArray()
        RefreshPathsList()
        PropagateTemplateEdit()
        Me.lstLibPthList.SelectedIndex = target
    End Sub

    ' Apply - bind the selected template to every checked endpoint: set binding.TemplateKey,
    ' stamp a copy of the template's paths, and re-expose the endpoint (applying a template
    ' is also the "unhide" gesture; hiding is the Visibility checkbox).
    Private Sub btnLibViwApply_Click(sender As Object, e As EventArgs)
        ' Single shared Templates list now drives both the Paths editor AND the apply
        ' target - the View sub-tab's apply reads from viewsTemplateCombo, same control
        ' the Paths sub-tab uses for editing.
        Dim selected As Plugin.BrowseTemplate = TryCast(Me.lstLibPthTemplate.SelectedItem, Plugin.BrowseTemplate)
        If selected Is Nothing Then Return
        Dim targets As New List(Of Plugin.EndpointBinding)
        For Each n As System.Windows.Forms.TreeNode In CollectAllEndpointNodes(Me.trvLibViwEndpoints.Nodes)
            If n.StateImageIndex = StateChecked Then
                Dim info As Plugin.EndpointInfo = TryCast(n.Tag, Plugin.EndpointInfo)
                ' Defensive: only stamp onto endpoints in the template's category (the tree
                ' already prevents checking out-of-category nodes, but never trust the UI alone).
                If info IsNot Nothing AndAlso Plugin.View.CategoryForEndpointType(info.Type) = selected.Category Then
                    Dim binding As Plugin.EndpointBinding = Plugin.View.GetBinding(info.Id)
                    If binding IsNot Nothing Then targets.Add(binding)
                End If
            End If
        Next
        If targets.Count = 0 Then Return
        For Each b As Plugin.EndpointBinding In targets
            b.TemplateKey = selected.Key
            b.Exposed = True
            b.Paths = Plugin.View.ClonePaths(selected.Paths)
        Next
        ' Refresh the tree so updated path-children + greyed text show up. Preserve the
        ' user's expanded state AND scroll position AND selection across the rebuild -
        ' LoadViewsTab clears + re-adds nodes, which loses all three. Capture by
        ' cumulative-text path so duplicate sibling names at different depths don't
        ' collide. BeginUpdate/EndUpdate suppresses the intermediate "scroll jumps to
        ' bottom" flash WinForms otherwise paints while we expand nodes one by one.
        Dim expandedPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim topPath As String = NodePath(Me.trvLibViwEndpoints.TopNode)
        Dim selectedPath As String = NodePath(Me.trvLibViwEndpoints.SelectedNode)
        CaptureExpandedPaths(Me.trvLibViwEndpoints.Nodes, "", expandedPaths)
        Me.trvLibViwEndpoints.BeginUpdate()
        Try
            LoadViewsTab()
            ' LoadViewsTab rebuilds the template list and resets it to the first entry - restore
            ' the template the user just applied so the selection doesn't jump back to the top.
            RefreshTemplateCombo(selected.Name)
            RestoreExpandedPaths(Me.trvLibViwEndpoints.Nodes, "", expandedPaths)
            ' Reselect first (so the selection doesn't auto-scroll the tree), then set
            ' TopNode last to pin the scroll position the user saw before Apply.
            If Not String.IsNullOrEmpty(selectedPath) Then
                Dim sel As System.Windows.Forms.TreeNode = FindNodeByPath(Me.trvLibViwEndpoints.Nodes, "", selectedPath)
                If sel IsNot Nothing Then Me.trvLibViwEndpoints.SelectedNode = sel
            End If
            If Not String.IsNullOrEmpty(topPath) Then
                Dim top As System.Windows.Forms.TreeNode = FindNodeByPath(Me.trvLibViwEndpoints.Nodes, "", topPath)
                If top IsNot Nothing Then Me.trvLibViwEndpoints.TopNode = top
            End If
        Finally
            Me.trvLibViwEndpoints.EndUpdate()
        End Try
    End Sub

    ' ── Visibility (①) + Placement (②) - tristate checkboxes over the checked set ──
    ' Each checkbox both REFLECTS and SETS a per-binding property, independent of the template:
    '   • visibility → binding.Exposed   (replaces the old "Hidden" reserved template; consumed
    '                                      live at browse time - a hidden endpoint isn't advertised)
    '   • top level  → binding.AtTopLevel (pin to the browse root; replaces the old global
    '                                      Expose-Filters/Playlists-at-root switches)
    ' AutoCheck is False, so a user click never auto-cycles into Indeterminate. The handler reads
    ' the displayed CheckState and resolves it to a definite target - Checked → turn the property
    ' OFF for the checked set, Unchecked/Indeterminate → turn it ON - applies it to every checked
    ' endpoint's binding, then re-reflects the checkbox from the (now uniform) selection.
    Private Sub chkLibViwVisible_Click(sender As Object, e As EventArgs)
        Dim turnOn As Boolean = (Me.chkLibViwVisible.CheckState <> System.Windows.Forms.CheckState.Checked)
        ApplyViewProperty(Sub(b) b.Exposed = turnOn)
    End Sub

    Private Sub chkLibViwTopLevel_Click(sender As Object, e As EventArgs)
        Dim turnOn As Boolean = (Me.chkLibViwTopLevel.CheckState <> System.Windows.Forms.CheckState.Checked)
        ApplyViewProperty(Sub(b) b.AtTopLevel = turnOn)
    End Sub

    ' Apply a binding mutation to every checked endpoint, then refresh that node's visuals in
    ' place (grey text = hidden, "★ " prefix = pinned to root). No full tree rebuild - neither
    ' property changes the tree's shape. Mutations hit the live View.EndpointBindings objects, so
    ' visibility takes effect on the next browse immediately; Save persists them to UPnPView.ini.
    Private Sub ApplyViewProperty(setter As Action(Of Plugin.EndpointBinding))
        Dim changed As Boolean = False
        For Each n As System.Windows.Forms.TreeNode In CollectAllEndpointNodes(Me.trvLibViwEndpoints.Nodes)
            If n.StateImageIndex <> StateChecked Then Continue For
            Dim info As Plugin.EndpointInfo = TryCast(n.Tag, Plugin.EndpointInfo)
            If info Is Nothing Then Continue For
            Dim b As Plugin.EndpointBinding = Plugin.View.GetBinding(info.Id)
            If b Is Nothing Then Continue For
            setter(b)
            Dim nodeColor As System.Drawing.Color = If(b.Exposed, System.Drawing.SystemColors.WindowText, System.Drawing.SystemColors.GrayText)
            n.ForeColor = nodeColor
            ' Path-children mirror the endpoint's exposed colour (black when visible, grey when hidden).
            For Each child As System.Windows.Forms.TreeNode In n.Nodes
                child.ForeColor = nodeColor
            Next
            n.Text = EndpointNodeText(info)
            changed = True
        Next
        If changed Then
            ' Roll the new visibility up into the folder labels (grey a folder once all its
            ' endpoints are hidden), then re-reflect the property checkboxes.
            RecolourAllGroupNodes()
            UpdateViewPropertyChecks()
        End If
    End Sub

    ' Reflect the two property checkboxes from the aggregate state of the currently CHECKED
    ' endpoints: all-on → Checked, all-off → Unchecked, mixed → Indeterminate (grey). With
    ' nothing checked the checkboxes are disabled - there's nothing to act on.
    Private Sub UpdateViewPropertyChecks()
        Dim exposedOn As Integer = 0, exposedOff As Integer = 0
        Dim topOn As Integer = 0, topOff As Integer = 0
        For Each n As System.Windows.Forms.TreeNode In CollectAllEndpointNodes(Me.trvLibViwEndpoints.Nodes)
            If n.StateImageIndex <> StateChecked Then Continue For
            Dim info As Plugin.EndpointInfo = TryCast(n.Tag, Plugin.EndpointInfo)
            If info Is Nothing Then Continue For
            Dim b As Plugin.EndpointBinding = Plugin.View.GetBinding(info.Id)
            If (b Is Nothing OrElse b.Exposed) Then exposedOn += 1 Else exposedOff += 1
            If (b IsNot Nothing AndAlso b.AtTopLevel) Then topOn += 1 Else topOff += 1
        Next
        Dim any As Boolean = (exposedOn + exposedOff) > 0
        Me.chkLibViwVisible.Enabled = any
        Me.chkLibViwTopLevel.Enabled = any
        Me.chkLibViwVisible.CheckState = AggregateCheckState(exposedOn, exposedOff)
        Me.chkLibViwTopLevel.CheckState = AggregateCheckState(topOn, topOff)
    End Sub

    Private Function AggregateCheckState(onCount As Integer, offCount As Integer) As System.Windows.Forms.CheckState
        If onCount > 0 AndAlso offCount = 0 Then Return System.Windows.Forms.CheckState.Checked
        If offCount > 0 AndAlso onCount = 0 Then Return System.Windows.Forms.CheckState.Unchecked
        Return System.Windows.Forms.CheckState.Indeterminate
    End Function

    ' Path segment identifying one node within its parent. Endpoint nodes use their stable
    ' endpoint Id - their Text mutates (★ pin marker, template-name suffix), which would
    ' break capture/restore across an Apply. Group/folder/path rows fall back to Text.
    Private Function NodeSegment(n As System.Windows.Forms.TreeNode) As String
        Dim info As Plugin.EndpointInfo = TryCast(n.Tag, Plugin.EndpointInfo)
        Return If(info IsNot Nothing, info.Id, n.Text)
    End Function

    ' Cumulative segment path for a node ("Playlists\Radio\playlist:Dragonfly Rock").
    ' Empty for Nothing.
    Private Function NodePath(node As System.Windows.Forms.TreeNode) As String
        If node Is Nothing Then Return ""
        Dim parts As New List(Of String)
        Dim n As System.Windows.Forms.TreeNode = node
        Do While n IsNot Nothing
            parts.Insert(0, NodeSegment(n))
            n = n.Parent
        Loop
        Return String.Join("\", parts)
    End Function

    ' Find the first node in the tree whose cumulative segment path matches `target`.
    ' Returns Nothing if no match (e.g. endpoint disappeared between capture and lookup).
    Private Function FindNodeByPath(nodes As System.Windows.Forms.TreeNodeCollection, parentPath As String, target As String) As System.Windows.Forms.TreeNode
        For Each n As System.Windows.Forms.TreeNode In nodes
            Dim p As String = If(parentPath.Length = 0, NodeSegment(n), parentPath & "\" & NodeSegment(n))
            If String.Equals(p, target, StringComparison.OrdinalIgnoreCase) Then Return n
            If n.Nodes.Count > 0 Then
                Dim hit As System.Windows.Forms.TreeNode = FindNodeByPath(n.Nodes, p, target)
                If hit IsNot Nothing Then Return hit
            End If
        Next
        Return Nothing
    End Function

    ' Walk every node; for each one whose IsExpanded = True, add its cumulative segment
    ' path (e.g. "Filters", "Playlists\Recently") to the set. Cumulative paths handle
    ' duplicate names at different depths (e.g. two "Folder" siblings under different
    ' parents don't collide).
    Private Sub CaptureExpandedPaths(nodes As System.Windows.Forms.TreeNodeCollection, parentPath As String, expanded As HashSet(Of String))
        For Each n As System.Windows.Forms.TreeNode In nodes
            Dim p As String = If(parentPath.Length = 0, NodeSegment(n), parentPath & "\" & NodeSegment(n))
            If n.IsExpanded Then expanded.Add(p)
            If n.Nodes.Count > 0 Then CaptureExpandedPaths(n.Nodes, p, expanded)
        Next
    End Sub

    ' Mirror of CaptureExpandedPaths: walk every node, expand if its path is in the set.
    ' Missing paths (e.g. endpoint deleted on disk between capture and restore) are
    ' silently ignored.
    Private Sub RestoreExpandedPaths(nodes As System.Windows.Forms.TreeNodeCollection, parentPath As String, expanded As HashSet(Of String))
        For Each n As System.Windows.Forms.TreeNode In nodes
            Dim p As String = If(parentPath.Length = 0, NodeSegment(n), parentPath & "\" & NodeSegment(n))
            If expanded.Contains(p) Then n.Expand()
            If n.Nodes.Count > 0 Then RestoreExpandedPaths(n.Nodes, p, expanded)
        Next
    End Sub

    ' Category of the template currently selected in the shared list (drives which view-tree
    ' nodes can be applied to). Standard when nothing is selected.
    Private Function SelectedTemplateCategory() As Plugin.PathCategory
        Dim sel As Plugin.BrowseTemplate = TryCast(Me.lstLibPthTemplate.SelectedItem, Plugin.BrowseTemplate)
        Return If(sel IsNot Nothing, sel.Category, Plugin.PathCategory.Standard)
    End Function

    ' Grey out + clear the checkbox of every endpoint whose category doesn't match the selected
    ' template's category, so a template can only be applied to nodes that can honour it.
    ' Matching endpoints regain their normal exposed-based colour. No-op before the tree is built.
    Private Sub RefreshViewTreeAvailability()
        If Me.trvLibViwEndpoints.Nodes.Count = 0 Then Return
        Dim cat As Plugin.PathCategory = SelectedTemplateCategory()
        For Each n As System.Windows.Forms.TreeNode In CollectAllEndpointNodes(Me.trvLibViwEndpoints.Nodes)
            Dim info As Plugin.EndpointInfo = TryCast(n.Tag, Plugin.EndpointInfo)
            If info Is Nothing Then Continue For
            Dim available As Boolean = (Plugin.View.CategoryForEndpointType(info.Type) = cat)
            Dim col As System.Drawing.Color
            If available Then
                Dim binding As Plugin.EndpointBinding = Plugin.View.GetBinding(info.Id)
                Dim exposed As Boolean = If(binding Is Nothing, True, binding.Exposed)
                col = If(exposed, System.Drawing.SystemColors.WindowText, System.Drawing.SystemColors.GrayText)
            Else
                ' Unavailable for this category - clear any pending check and grey it.
                n.StateImageIndex = StateUnchecked
                col = System.Drawing.SystemColors.GrayText
            End If
            n.ForeColor = col
            For Each child As System.Windows.Forms.TreeNode In n.Nodes
                child.ForeColor = col
            Next
        Next
        RecomputeAllParentStates()
        RecolourAllGroupNodes()
        ' Grey group/folder rows that hold no in-category endpoint (RecolourAllGroupNodes only
        ' considers exposed-state), so whole unavailable branches read inert, not just the leaves.
        GreyUnavailableGroups(Me.trvLibViwEndpoints.Nodes, cat)
        UpdateViewPropertyChecks()
    End Sub

    ' Grey every group/folder node with no in-category endpoint beneath it. Returns True if this
    ' collection contains at least one available endpoint (so parents can tell if they're inert).
    ' Endpoint colours are owned by RefreshViewTreeAvailability; this only recolours group rows.
    Private Function GreyUnavailableGroups(nodes As System.Windows.Forms.TreeNodeCollection, cat As Plugin.PathCategory) As Boolean
        Dim anyAvailable As Boolean = False
        For Each n As System.Windows.Forms.TreeNode In nodes
            Dim info As Plugin.EndpointInfo = TryCast(n.Tag, Plugin.EndpointInfo)
            If info IsNot Nothing Then
                If Plugin.View.CategoryForEndpointType(info.Type) = cat Then anyAvailable = True
            ElseIf n.Tag IsNot Nothing Then
                ' Group / folder node - recurse, then grey it if nothing in-category lives under it.
                If GreyUnavailableGroups(n.Nodes, cat) Then
                    anyAvailable = True
                Else
                    n.ForeColor = System.Drawing.SystemColors.GrayText
                End If
            End If
        Next
        Return anyAvailable
    End Function

    ' Set the checkbox of every AVAILABLE (in selected-category) endpoint leaf under `node` to
    ' targetState. Out-of-category leaves are left untouched (RefreshViewTreeAvailability keeps
    ' them unchecked), so a group click can never select a node its template can't apply to;
    ' the group's own state is then derived by RecomputeNodeState.
    Private Sub SetAvailableEndpointsInSubtree(node As System.Windows.Forms.TreeNode, targetState As Integer)
        Dim cat As Plugin.PathCategory = SelectedTemplateCategory()
        For Each ep As System.Windows.Forms.TreeNode In CollectAllEndpointNodes(node.Nodes)
            Dim info As Plugin.EndpointInfo = TryCast(ep.Tag, Plugin.EndpointInfo)
            If info IsNot Nothing AndAlso Plugin.View.CategoryForEndpointType(info.Type) = cat Then
                ep.StateImageIndex = targetState
            End If
        Next
    End Sub

    Private Function CollectAllEndpointNodes(nodes As System.Windows.Forms.TreeNodeCollection) As List(Of System.Windows.Forms.TreeNode)
        Dim list As New List(Of System.Windows.Forms.TreeNode)
        For Each n As System.Windows.Forms.TreeNode In nodes
            If TypeOf n.Tag Is Plugin.EndpointInfo Then list.Add(n)
            If n.Nodes.Count > 0 Then list.AddRange(CollectAllEndpointNodes(n.Nodes))
        Next
        Return list
    End Function

    ' StateImage click handler. Used because we run with CheckBoxes=False (so the native
    ' Before/AfterCheck pipeline doesn't fire). The state image hit-test region is precise:
    ' clicking elsewhere on the row leaves selection alone but doesn't toggle.
    '
    ' Click only changes SELECTION (the checkbox state image). It does NOT touch
    ' binding.Exposed or ForeColor - those are owned by Apply and reflect actual state.
    '   Endpoint click:    toggle this node (checked ↔ unchecked), roll state up.
    '   Group/folder click: tri-state cycle - Unchecked → check all descendants.
    '                       Checked → uncheck all. Mixed → check all (treat partial as
    '                       an incomplete set the user wants to finish). Roll state up.
    Private Sub trvLibViwEndpoints_NodeMouseClick(sender As Object, e As System.Windows.Forms.TreeNodeMouseClickEventArgs)
        If e.Button <> System.Windows.Forms.MouseButtons.Left Then Return
        If viewsSuppressEvents Then Return
        Dim hit As System.Windows.Forms.TreeViewHitTestInfo = Me.trvLibViwEndpoints.HitTest(e.X, e.Y)
        If hit.Location <> System.Windows.Forms.TreeViewHitTestLocations.StateImage Then Return
        Dim n As System.Windows.Forms.TreeNode = e.Node
        If n.Tag Is Nothing Then Return  ' path-child - un-checkable
        ' An endpoint outside the selected template's category can't be applied to → not checkable.
        If TypeOf n.Tag Is Plugin.EndpointInfo Then
            Dim epInfo As Plugin.EndpointInfo = DirectCast(n.Tag, Plugin.EndpointInfo)
            If Plugin.View.CategoryForEndpointType(epInfo.Type) <> SelectedTemplateCategory() Then Return
        End If
        viewsSuppressEvents = True
        Try
            If TypeOf n.Tag Is Plugin.EndpointInfo Then
                n.StateImageIndex = If(n.StateImageIndex = StateChecked, StateUnchecked, StateChecked)
            Else
                ' Group/folder click: only (un)check the in-category endpoint leaves beneath it,
                ' then DERIVE this node's + its sub-folders' state from those leaves. A group with
                ' no in-category endpoints recomputes to Unchecked, so it can't be checked at all.
                Dim targetState As Integer = If(n.StateImageIndex = StateChecked, StateUnchecked, StateChecked)
                SetAvailableEndpointsInSubtree(n, targetState)
                RecomputeNodeState(n)
            End If
            RecomputeAncestors(n)
        Finally
            viewsSuppressEvents = False
        End Try
        ' Selection changed → re-reflect the Visible / Top level checkboxes for the new checked set.
        UpdateViewPropertyChecks()
    End Sub

    ' Small modal text prompt - WinForms doesn't ship one, so we build a 3-control dialog.
    Private Function PromptForName(promptText As String, defaultValue As String) As String
        Using dlg As New System.Windows.Forms.Form() With {
            .Text = Plugin.L("MessageBoxTitle"),
            .FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            .StartPosition = System.Windows.Forms.FormStartPosition.CenterParent,
            .MinimizeBox = False,
            .MaximizeBox = False,
            .ClientSize = New System.Drawing.Size(320, 110)
        }
            Dim lbl As New System.Windows.Forms.Label() With {
                .AutoSize = True,
                .Location = New System.Drawing.Point(12, 12),
                .Text = promptText
            }
            Dim txt As New System.Windows.Forms.TextBox() With {
                .Location = New System.Drawing.Point(12, 35),
                .Size = New System.Drawing.Size(296, 20),
                .Text = defaultValue
            }
            Dim okBtn As New System.Windows.Forms.Button() With {
                .Location = New System.Drawing.Point(152, 70),
                .Size = New System.Drawing.Size(75, 23),
                .Text = "OK",
                .DialogResult = System.Windows.Forms.DialogResult.OK
            }
            Dim cancelBtn As New System.Windows.Forms.Button() With {
                .Location = New System.Drawing.Point(233, 70),
                .Size = New System.Drawing.Size(75, 23),
                .Text = "Cancel",
                .DialogResult = System.Windows.Forms.DialogResult.Cancel
            }
            dlg.Controls.AddRange(New System.Windows.Forms.Control() {lbl, txt, okBtn, cancelBtn})
            dlg.AcceptButton = okBtn
            dlg.CancelButton = cancelBtn
            If dlg.ShowDialog(Me) = System.Windows.Forms.DialogResult.OK Then
                Return txt.Text.Trim()
            End If
        End Using
        Return Nothing
    End Function

    Private Sub btnDevAddProfile_Click(sender As Object, e As EventArgs)
        Dim profile As New Plugin.StreamingProfile(Plugin.L("NewProfileName"))
        Plugin.Settings.StreamingProfiles.Add(profile)
        Me.lstDevStreamingProfiles.Items.Add(profile)
        Me.lstDevStreamingProfiles.SelectedIndex = Me.lstDevStreamingProfiles.Items.Count - 1
        Me.txtDevUserAgent.Text = Plugin.L("txtDevUserAgent")
    End Sub

    Private Sub btnDevRemoveProfile_Click(sender As Object, e As EventArgs)
        Dim index As Integer = Me.lstDevStreamingProfiles.SelectedIndex
        If index > 0 Then
            Me.lstDevStreamingProfiles.SelectedIndex = index - 1
            Plugin.Settings.StreamingProfiles.RemoveAt(index)
            Me.lstDevStreamingProfiles.Items.RemoveAt(index)
        End If
    End Sub

    Private Sub txtDevPictureSize_Leave(sender As Object, e As EventArgs)
        If Me.txtDevPictureSize.Text <> lastPictureSize Then
            lastPictureSize = Me.txtDevPictureSize.Text
            If lastPictureSize <> "160" Then
                MessageBox.Show(Me, Plugin.L("WarnPictureSize"), Plugin.L("MessageBoxTitle"))
            End If
        End If
    End Sub

    Private Sub btnDbgView_Click(sender As Object, e As EventArgs)
        Process.Start("notepad.exe", """" & Plugin.mbApiInterface.Setting_GetPersistentStoragePath() & "UpnpErrorLog.dat""")
    End Sub

    ' Scan UpnpErrorLog.dat backwards for the most recent "LazyQuery" line and paste
    ' its query payload into the XML input box, ready to re-run with the Run button.
    ' Log line format: "<gap>; <counter> LazyQuery - [<context>] returned=<bool> count=<n>  Q: <query>"
    Private Sub btnDbgReadLastQuery_Click(sender As Object, e As EventArgs)
        Try
            Dim path As String = Plugin.mbApiInterface.Setting_GetPersistentStoragePath() & "UpnpErrorLog.dat"
            If Not IO.File.Exists(path) Then
                Me.lblDbgXmlStatus.Text = "Log file not found"
                Return
            End If
            Dim found As String = Nothing
            ' Read lines, scan from end. ReadAllLines is fine - log isn't huge in practice
            ' and even a 5MB log is sub-100ms.
            Dim lines() As String = IO.File.ReadAllLines(path)
            For i As Integer = lines.Length - 1 To 0 Step -1
                Dim line As String = lines(i)
                Dim tag As String = " LazyQuery - "
                Dim tagIdx As Integer = line.IndexOf(tag, StringComparison.Ordinal)
                If tagIdx < 0 Then Continue For
                Dim qMarkerIdx As Integer = line.IndexOf("  Q: ", tagIdx, StringComparison.Ordinal)
                If qMarkerIdx < 0 Then Continue For
                found = line.Substring(qMarkerIdx + "  Q: ".Length)
                Exit For
            Next
            If found Is Nothing Then
                Me.lblDbgXmlStatus.Text = "No LazyQuery entries in log (toggle 'log debug information')"
                Return
            End If
            ' Pretty-print XML for readability. Plain "domain=…" queries aren't XML -
            ' XDocument.Parse will throw, and we keep the original string.
            Dim pretty As String = found
            Try
                Dim doc As XDocument = XDocument.Parse(found)
                pretty = doc.ToString(SaveOptions.None)
            Catch
                ' Not XML - leave as-is.
            End Try
            Me.txtDbgXmlInput.Text = pretty
            Me.lblDbgXmlStatus.Text = "Loaded last query (length=" & found.Length & ")"
        Catch ex As Exception
            Me.lblDbgXmlStatus.Text = "Read failed: " & ex.Message
        End Try
    End Sub

    ' Truncate UpnpErrorLog.dat. Useful when starting a new debug session - old noise
    ' gets in the way of seeing the queries you're investigating right now.
    Private Sub btnDbgClearLog_Click(sender As Object, e As EventArgs)
        Try
            Dim path As String = Plugin.mbApiInterface.Setting_GetPersistentStoragePath() & "UpnpErrorLog.dat"
            IO.File.WriteAllText(path, "")
        Catch ex As Exception
            System.Windows.Forms.MessageBox.Show(Me, "Could not clear log: " & ex.Message, "Debug", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning)
        End Try
    End Sub

    ' URLs from the most recent successful debugXmlRunButton run - used by the
    ' "Fetch tags for first N" follow-up button. Held on the form so the user can
    ' run a query, look at the URLs, then dig into specific track tags without
    ' re-running the (potentially slow) query.
    Private debugXmlLastUrls() As String = New String() {}

    ' Submit the XML in debugXmlInputBox to Library_QueryFilesEx and display the
    ' result. Accepts EITHER a smart-playlist XML blob OR a plain "domain=…" query
    ' string (whichever the user pastes). No transformation - what's in the box is
    ' what gets sent verbatim, so we see EXACTLY what MB does.
    Private Sub btnDbgXmlRun_Click(sender As Object, e As EventArgs)
        Dim query As String = If(Me.txtDbgXmlInput.Text, "").Trim()
        Dim urls() As String = Nothing
        Dim ok As Boolean = False
        Try
            ok = Plugin.mbApiInterface.Library_QueryFilesEx(query, urls)
        Catch ex As Exception
            Me.lblDbgXmlStatus.Text = "Exception: " & ex.Message
            Me.txtDbgXmlResults.Text = ""
            debugXmlLastUrls = New String() {}
            Return
        End Try
        If urls Is Nothing Then urls = New String() {}
        debugXmlLastUrls = urls
        Me.lblDbgXmlStatus.Text = "returned=" & ok.ToString() & "  count=" & urls.Length
        ' Cap displayed list at 200 lines so a 10k-result query doesn't freeze the textbox.
        Dim cap As Integer = Math.Min(urls.Length, 200)
        Dim sb As New System.Text.StringBuilder
        For i As Integer = 0 To cap - 1
            sb.AppendLine("[" & i & "] " & urls(i))
        Next
        If urls.Length > cap Then sb.AppendLine("…(" & (urls.Length - cap) & " more not shown)")
        Me.txtDbgXmlResults.Text = sb.ToString()
        Me.txtDbgXmlTagsResult.Text = ""
    End Sub

    ' Fetch the full 80-tag block via Library_GetFileTags for the first few URLs
    ' from the last query, dumping every field labelled. Lets the user verify what
    ' the lazy code paths actually receive per track (vs what they expect).
    Private Sub btnDbgXmlFetchTags_Click(sender As Object, e As EventArgs)
        If debugXmlLastUrls Is Nothing OrElse debugXmlLastUrls.Length = 0 Then
            Me.txtDbgXmlTagsResult.Text = "Run a query first."
            Return
        End If
        Dim n As Integer = Math.Min(5, debugXmlLastUrls.Length)
        Dim sb As New System.Text.StringBuilder
        For i As Integer = 0 To n - 1
            Dim url As String = debugXmlLastUrls(i)
            sb.AppendLine("=== Track " & i & ": " & url)
            ' Iterate every MetaDataType value with a positive int code (skip None=0
            ' and reserved/extension values that MB rejects). Use Library_GetFileTag
            ' (single-tag) so we see each field labeled individually rather than a
            ' positional dump from Library_GetFileTags.
            For Each mdt As Plugin.MetaDataType In [Enum].GetValues(GetType(Plugin.MetaDataType))
                Dim code As Integer = CInt(mdt)
                If code <= 0 Then Continue For
                Dim v As String = Nothing
                Try
                    v = Plugin.mbApiInterface.Library_GetFileTag(url, mdt)
                Catch
                    Continue For
                End Try
                If String.IsNullOrEmpty(v) Then Continue For
                sb.AppendLine("  " & [Enum].GetName(GetType(Plugin.MetaDataType), mdt) & " = " & v)
            Next
            sb.AppendLine()
        Next
        Me.txtDbgXmlTagsResult.Text = sb.ToString()
    End Sub

    ' ===== Update notification (bottom-left) =====
    ' Mirrors the Electron apps' banner: poll the version-only beacon on a background
    ' thread; if a newer version is published, reveal the label + "What's new" /
    ' "Download" links. The links open the localized release pages (latest.html /
    ' download.html). Best-effort - silent on any failure (404 / offline / parse).
    Private Sub StartUpdateCheck()
        Dim t As New System.Threading.Thread(AddressOf UpdateCheckWorker)
        t.IsBackground = True
        t.Start()
    End Sub

    Private Sub UpdateCheckWorker()
        Try
            Dim latest As String = Nothing
            If Not Plugin.UpdateCheck.TryGetLatestVersion(Plugin.UpdateCheck.AppId, latest) Then Return
            If Not Plugin.UpdateCheck.IsNewer(latest, Plugin.UpdateCheck.CurrentVersion()) Then Return
            If Me.IsHandleCreated Then
                Me.BeginInvoke(New Action(Sub() ShowUpdateAvailable(latest)))
            End If
        Catch
            ' silent - never surface an update-check failure to the user
        End Try
    End Sub

    Private Sub ShowUpdateAvailable(latestVersion As String)
        Me.lblUpdateAvailable.Text = Plugin.L("lblUpdateAvailable") & " v" & latestVersion
        Me.lblUpdateAvailable.Visible = True
        Me.lnkUpdateWhatsNew.Visible = True
        Me.lnkUpdateDownload.Visible = True
    End Sub

    Private Sub lnkUpdateWhatsNew_LinkClicked(sender As Object, e As LinkLabelLinkClickedEventArgs)
        OpenUrl(Plugin.UpdateCheck.GetUrl(Plugin.UpdateCheck.AppId, Plugin.Localisation.DetectMusicBeeLanguage(), "help/releases"))
    End Sub

    Private Sub lnkUpdateDownload_LinkClicked(sender As Object, e As LinkLabelLinkClickedEventArgs)
        OpenUrl(Plugin.UpdateCheck.GetUrl(Plugin.UpdateCheck.AppId, Plugin.Localisation.DetectMusicBeeLanguage(), "download"))
    End Sub

    Private Sub OpenUrl(url As String)
        Try
            System.Diagnostics.Process.Start(url)
        Catch
            ' silent
        End Try
    End Sub

    ' Opens the online help page for the plugin in the user's MusicBee language
    ' (apps.yaiol.com/<lang>/p/musicbee-upnp-plugin/help/), mirroring the Electron / browser-ext
    ' help button. The help site falls back to EN for any language not yet built.
    Private Sub btnHelp_Click(sender As Object, e As EventArgs)
        Try
            Dim culture As String = Plugin.Localisation.DetectMusicBeeLanguage()
            System.Diagnostics.Process.Start(Plugin.UpdateCheck.GetUrl(Plugin.UpdateCheck.AppId, culture, "help"))
        Catch
            ' silent
        End Try
    End Sub

    ' Opens the plugin's source repository on GitHub in the user's default browser.
    Private Sub btnGithub_Click(sender As Object, e As EventArgs)
        Try
            System.Diagnostics.Process.Start(Plugin.UpdateCheck.RepoUrl)
        Catch
            ' silent
        End Try
    End Sub

    Private Sub btnClose_Click(sender As Object, e As EventArgs)
        ' Cancel = discard. Template/path edits mutate the live Plugin.View model directly (no
        ' staging copy), so on Cancel we re-read it from disk to throw those edits away. This
        ' also re-runs the safety net, restoring any reserved template the user deleted this
        ' session. Without this, a cancelled edit lingers in memory and a later save persists it.
        Plugin.View.Load()
        isDirty = False
        Me.Close()
    End Sub

    Private Sub btnSave_Click(sender As Object, e As EventArgs)
        isDirty = False
        SaveSettings()
        Me.Close()
    End Sub

    Private Sub SaveSettings()
        Me.Enabled = False
        Me.Cursor = Cursors.WaitCursor
        Me.Update()
        Try
            Plugin.Settings.EnablePlayToDevice = Me.chkEnableController.Checked
            Plugin.Settings.EnableMediaRenderer = Me.chkEnableMediaRenderer.Checked
            Plugin.Settings.ContinuousOutput = Me.chkPbkContinuousStream.Checked
            Plugin.Settings.ServerName = Me.txtSrvName.Text
            Plugin.Settings.RendererName = Me.txtRendererName.Text
            Plugin.Settings.IpAddress = If(Me.cboSrvIpAddress.SelectedIndex <= 0, "", Me.cboSrvIpAddress.SelectedItem.ToString())
            If Not Integer.TryParse(Me.txtSrvPort.Text, Plugin.Settings.ServerPort) Then
                Plugin.Settings.ServerPort = 9779
            End If
            Plugin.Settings.MaxConnections = CInt(Me.numSrvMaxConnections.Value)
            Plugin.Settings.DefaultProfileIndex = Me.lstDevStreamingProfiles.SelectedIndex
            UpdateCurrentStreamingProfile()
            Plugin.Settings.EnableContentAccess = Me.chkSrvEnableBrowse.Checked
            Plugin.Settings.ServerUpdatePlayStatistics = Me.chkLibOptSubmitPlayStats.Checked
            Plugin.Settings.FilterPrefix = Me.txtLibOptFilterPrefix.Text
            Plugin.Settings.PlaylistPrefix = Me.txtLibOptPlaylistPrefix.Text
            ' Index 0 is "(All Music)" = no filter; anything else is the filter basename itself.
            Plugin.Settings.RandomSourceFilter = If(Me.cboLibOptRandomSource.SelectedIndex <= 0, "", Me.cboLibOptRandomSource.SelectedItem.ToString())
            Plugin.Settings.HierarchicalFields = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            If libOptHierMap IsNot Nothing Then
                For Each kv As KeyValuePair(Of String, String) In libOptHierMap
                    Plugin.Settings.HierarchicalFields(kv.Key) = kv.Value
                Next
            End If
            ' EQ/DSP and ReplayGain are per-profile - saved via UpdateCurrentStreamingProfile().
            ' Filter/playlist hide flows through Plugin.EndpointBinding.Exposed (Views tab
            ' Visibility checkbox). No separate "hidden filters" list to rebuild here anymore.
            ' BucketNodes / BucketTrigger are compile-time constants now (legacy Music tree only).
            Plugin.Settings.BandwidthConstrained = Me.chkPbkBandwidthIsConstrained.Checked
            Plugin.Settings.ForceNativeStreamForRadio = Me.chkPbkForceNativeStreamForRadio.Checked
            Plugin.Settings.LogDebugInfo = Me.chkDbgInfo.Checked
            Plugin.Settings.ClearLogOnStartup = Me.chkDbgClearLogOnStartup.Checked
            Plugin.Settings.SaveSettings()
            ' Detect restart-required settings that diverged from their snapshotted runtime
            ' values. Once flipped, Plugin.RestartRequired stays True until MusicBee restart
            ' so the badge keeps reminding the user.
            If Plugin.activeMaxConnections <> Plugin.Settings.MaxConnections _
                OrElse Plugin.activeServerPort <> Plugin.Settings.ServerPort _
                OrElse Plugin.activeEnableMediaRenderer <> Plugin.Settings.EnableMediaRenderer _
                OrElse Not String.Equals(Plugin.activeIpAddress, Plugin.Settings.IpAddress, StringComparison.OrdinalIgnoreCase) Then
                Plugin.RestartRequired = True
            End If
            ' Clear the in-memory tree cache so changes to filter visibility / profiles take
            ' effect immediately. Without this, the cached Plugin.ItemManager keeps serving the old
            ' tree shape across the Plugin.server restart.
            Plugin.ItemManager.ResetCache()
            Dim restartThread As New Thread(AddressOf RestartServer)
            restartThread.IsBackground = True
            restartThread.Start()
            Threading.Thread.Sleep(200)
        Finally
            Me.Enabled = True
            Me.Cursor = Cursors.Default
        End Try
    End Sub

    Private Sub RestartServer()
        ' controller / server can be Nothing if the initial Initialise threw before
        ' creating them (e.g. the configured port was unavailable at startup). In that
        ' case recreate-and-start here, so saving a working port revives the plugin
        ' without forcing a full MusicBee restart. Otherwise just restart in place.
        Try
            If Plugin.controller Is Nothing Then
                Plugin.controller = New Plugin.ControlPointManager()
                Plugin.controller.Start()
            Else
                Plugin.controller.Restart()
            End If
        Catch ex As Exception
            Plugin.LogError(ex, "RestartController")
        End Try
        Try
            If Plugin.server Is Nothing Then
                Plugin.server = New Plugin.MediaServerDevice(Plugin.Settings.Udn)
                Plugin.server.Start()
            Else
                Plugin.server.Restart(True)
            End If
        Catch ex As Exception
            Plugin.LogError(ex, "RestartServer")
        End Try
    End Sub

    Private Function UpdateCurrentStreamingProfile() As Plugin.StreamingProfile
        Dim profile As Plugin.StreamingProfile = DirectCast(Me.lstDevStreamingProfiles.Items(lastProfileIndex), Plugin.StreamingProfile)
        profile.ProfileName = Me.txtDevProfileName.Text
        profile.UserAgents = Me.txtDevUserAgent.Text.Split(New Char() {"|"c}, StringSplitOptions.RemoveEmptyEntries)
        For index As Integer = 0 To profile.UserAgents.Length - 1
            profile.UserAgents(index) = profile.UserAgents(index).Trim()
        Next index
        If Not UShort.TryParse(Me.txtDevPictureSize.Text, profile.PictureSize) Then
            profile.PictureSize = 160
        End If
        profile.MinimumSampleRate = CInt(Me.cboDevSampleRateFrom.SelectedItem.ToString())
        profile.MaximumSampleRate = CInt(Me.cboDevSampleRateTo.SelectedItem.ToString())
        profile.StereoOnly = Me.chkDevStereoOnly.Checked
        profile.MaximumBitDepth = CInt(Me.cboDevMaxBitDepth.SelectedItem.ToString())
        profile.TranscodeBitDepth = 16
        Select Case Me.cboDevTranscodeFormat.SelectedIndex
            Case 0
                profile.TranscodeCodec = Plugin.FileCodec.Pcm
            Case 1
                profile.TranscodeCodec = Plugin.FileCodec.Pcm
                profile.TranscodeBitDepth = If(profile.MaximumBitDepth = 16, 16, 24)
            Case 2
                profile.TranscodeCodec = Plugin.FileCodec.Mp3
            Case 3
                profile.TranscodeCodec = Plugin.FileCodec.Aac
            Case 4
                profile.TranscodeCodec = Plugin.FileCodec.Ogg
            Case 5
                profile.TranscodeCodec = Plugin.FileCodec.Flac
        End Select
        profile.TranscodeSampleRate = If(Me.cboDevTranscodeSampleRate.SelectedIndex = Me.cboDevTranscodeSampleRate.Items.Count - 1, -1, CInt(Me.cboDevTranscodeSampleRate.SelectedItem.ToString()))
        profile.DoNotUseRawPcm = Me.chkDevDoNotUseRawPcm.Checked
        profile.ForceLittleEndianPcm = Me.chkDevForceLittleEndianPcm.Checked
        profile.ContentLength = DirectCast(Me.cboDevContentLength.SelectedIndex, Plugin.ContentLengthMode)
        profile.ForceNativeStream = Me.chkDevForceNativeStream.Checked
        profile.ForceTranscoding = Me.chkDevForceTranscoding.Checked
        profile.EnableNextUri = Me.chkDevEnableNextUri.Checked
        profile.DoNotClearNextUri = Me.chkDevDoNotClearNextUri.Checked
        profile.EnableSoundEffects = Me.chkDevEnableSoundEffects.Checked
        profile.EnableReplayGain = Me.chkDevEnableReplayGain.Checked
        Return profile
    End Function

    ' ===== Post-designer wiring =====
    ' Everything below was inline in the hand-written InitializeComponent before the designer
    ' refactor. It lives here so the .Designer.vb file stays roundtrip-clean for VS.
    Private Sub ApplyDesignerExtras()
        ' --- Localized text (from Plugin.L(key), set at runtime so the designer doesn't
        ' have to know about the string bundles) ---
        ' Window title = the product name, from the assembly (UpdateCheck.ProductName() ← AssemblyProduct
        ' ← info.json name). Single source, brand string, identical in every language, NOT translated.
        ' ⚠ CLAUDE: "yaiol" is NEVER part of a product name (everything here is yaiol - redundant); it
        ' survives only in the DLL filename (AssemblyName mb_UPnP_yaiol). Don't hardcode the name here.
        Me.Text = Plugin.UpdateCheck.ProductName() & " v" & Plugin.UpdateCheck.DisplayVersion()
        Me.lblUpdateAvailable.Text = Plugin.L("lblUpdateAvailable")
        Me.lnkUpdateWhatsNew.Text = Plugin.L("lnkUpdateWhatsNew")
        Me.lnkUpdateDownload.Text = Plugin.L("lnkUpdateDownload")
        Me.tabGeneral.Text = Plugin.L("tabGeneral")
        Me.tabPlayback.Text = Plugin.L("tabPlayback")
        Me.tabProfiles.Text = Plugin.L("tabProfiles")
        Me.tabLibraryGeneral.Text = Plugin.L("tabLibraryGeneral")
        Me.tabLibraryPaths.Text = Plugin.L("tabLibraryPaths")
        Me.tabUPnPPaths.Text = Plugin.L("tabUPnPPaths")
        Me.tabUPnPView.Text = Plugin.L("tabUPnPView")
        Me.tabDebug.Text = Plugin.L("tabDebug")
        Me.btnDbgClearLog.Text = Plugin.L("btnDbgClearLog")
        Me.btnDbgReadLastQuery.Text = Plugin.L("btnDbgReadLastQuery")
        Me.btnDbgXmlRun.Text = Plugin.L("btnDbgXmlRun")
        Me.btnDbgXmlFetchTags.Text = Plugin.L("btnDbgXmlFetchTags")
        Me.tabDevice.Text = Plugin.L("tabDevice")
        Me.tabTranscoding.Text = Plugin.L("tabTranscoding")
        Me.tabAdvanced.Text = Plugin.L("tabAdvanced")
        Me.lblSrvSettings.Text = Plugin.L("lblSrvSettings")
        Me.lblSrvName.Text = Plugin.L("lblSrvName")
        Me.lblRendererName.Text = Plugin.L("lblRendererName")
        Me.lblSrvIpAddress.Text = Plugin.L("lblSrvIpAddress")
        Me.lblSrvPort.Text = Plugin.L("lblSrvPort")
        Me.lblSrvMaxConnections.Text = Plugin.L("lblSrvMaxConnections")
        Me.chkEnableController.Text = Plugin.L("chkEnableController")
        Me.chkEnableMediaRenderer.Text = Plugin.L("chkEnableMediaRenderer")
        Me.chkPbkContinuousStream.Text = Plugin.L("chkPbkContinuousStream")
        Me.lblPbkContinuousStream.Text = Plugin.L("lblPbkContinuousStream")
        Me.chkPbkBandwidthIsConstrained.Text = Plugin.L("chkPbkBandwidthIsConstrained")
        Me.chkPbkForceNativeStreamForRadio.Text = Plugin.L("chkPbkForceNativeStreamForRadio")
        Me.chkSrvEnableBrowse.Text = Plugin.L("chkSrvEnableBrowse")
        Me.chkLibOptSubmitPlayStats.Text = Plugin.L("chkLibOptSubmitPlayStats")
        Me.lblLibOptFilterPrefix.Text = Plugin.L("lblLibOptFilterPrefix")
        Me.lblLibOptPlaylistPrefix.Text = Plugin.L("lblLibOptPlaylistPrefix")
        Me.lblLibOptRandomSource.Text = Plugin.L("lblLibOptRandomSource")
        Me.lblLibOptHierFields.Text = Plugin.L("lblLibOptHierFields")
        Me.lblLibPthName.Text = Plugin.L("lblLibPthName")
        Me.lblLibPthNameCollision.Text = Plugin.L("lblLibPthNameCollision")
        Me.lblLibPthTemplate.Text = Plugin.L("lblLibPthTemplate")
        ' btnLibPthAdd / btnLibPthRemove are icon-only - image + tooltip applied below.
        Me.lblLibPthHierarchy.Text = Plugin.L("lblLibPthHierarchy")
        Me.lblLibPthLeaf.Text = Plugin.L("lblLibPthLeaf")
        Me.radLibPthLeafAT.Text = Plugin.L("radLibPthLeafAT")
        Me.radLibPthLeafT.Text = Plugin.L("radLibPthLeafT")
        Me.chkLibPthIncludeAllTracks.Text = Plugin.L("chkLibPthIncludeAllTracks")
        Me.lblLibPthAlbumGroup.Text = Plugin.L("lblLibPthAlbumGroup")
        ' btnLibViwExpandAll / btnLibViwCollapseAll are icon-only - image + tooltip applied below.
        Me.btnLibViwApply.Text = Plugin.L("btnLibViwApply")
        Me.chkLibViwVisible.Text = Plugin.L("chkLibViwVisible")
        Me.chkLibViwTopLevel.Text = Plugin.L("chkLibViwTopLevel")
        Me.lblDevStreamingProfiles.Text = Plugin.L("lblDevStreamingProfiles")
        Me.btnDevAddProfile.Text = Plugin.L("btnDevAddProfile")
        Me.btnDevRemoveProfile.Text = Plugin.L("btnDevRemoveProfile")
        Me.lblDevProfileName.Text = Plugin.L("lblDevProfileName")
        Me.lblDevUserAgent.Text = Plugin.L("lblDevUserAgent")
        Me.lblDevCapabilities.Text = Plugin.L("lblDevCapabilities")
        Me.lblDevPictureSize.Text = Plugin.L("lblDevPictureSize")
        Me.lblDevPictureSizeUnit.Text = Plugin.L("lblDevPictureSizeUnit")
        Me.lblDevSampleRateFrom.Text = Plugin.L("lblDevSampleRateFrom")
        Me.lblDevSampleRateTo.Text = Plugin.L("lblDevSampleRateTo")
        Me.lblDevChannels.Text = Plugin.L("lblDevChannels")
        Me.chkDevStereoOnly.Text = Plugin.L("chkDevStereoOnly")
        Me.lblDevMaxBitDepth.Text = Plugin.L("lblDevMaxBitDepth")
        Me.lblDevTranscoding.Text = Plugin.L("lblDevTranscoding")
        Me.lblDevTranscodeFormat.Text = Plugin.L("lblDevTranscodeFormat")
        Me.lblDevTranscodeSampleRate.Text = Plugin.L("lblDevTranscodeSampleRate")
        Me.chkDevForceNativeStream.Text = Plugin.L("chkDevForceNativeStream")
        Me.chkDevForceTranscoding.Text = Plugin.L("chkDevForceTranscoding")
        Me.chkDevEnableSoundEffects.Text = Plugin.L("chkDevEnableSoundEffects")
        Me.chkDevEnableReplayGain.Text = Plugin.L("chkDevEnableReplayGain")
        Me.chkDevEnableNextUri.Text = Plugin.L("chkDevEnableNextUri")
        Me.chkDevDoNotClearNextUri.Text = Plugin.L("chkDevDoNotClearNextUri")
        Me.lblDevProblems.Text = Plugin.L("lblDevProblems")
        Me.chkDevDoNotUseRawPcm.Text = Plugin.L("chkDevDoNotUseRawPcm")
        Me.chkDevForceLittleEndianPcm.Text = Plugin.L("chkDevForceLittleEndianPcm")
        Me.lblDevContentLength.Text = Plugin.L("lblDevContentLength")
        Me.chkDbgInfo.Text = Plugin.L("chkDbgInfo")
        Me.chkDbgClearLogOnStartup.Text = Plugin.L("chkDbgClearLogOnStartup")
        Me.btnDbgView.Text = Plugin.L("btnDbgView")
        Me.btnSave.Text = Plugin.L("btnSave")
        Me.btnClose.Text = Plugin.L("btnClose")
        Me.lblMaxConnectionsBadge.Text = Plugin.L("lblMaxConnectionsBadge")
        Me.lblRestartRequiredBadge.Text = Plugin.L("lblRestartRequiredBadge")
        ' --- Items populated dynamically (designer can't know these) ---
        Me.cboDevContentLength.Items.AddRange(New Object() {
            Plugin.L("ContentLengthDefault"),
            Plugin.L("ContentLengthNone"),
            Plugin.L("ContentLengthPcmOnly"),
            Plugin.L("ContentLengthFixed")})
        ' --- Per-control event handlers that were inline in the original InitializeComponent ---
        AddHandler Me.btnLibViwExpandAll.Click, AddressOf btnLibViwExpandAll_Click
        AddHandler Me.btnLibViwCollapseAll.Click, AddressOf btnLibViwCollapseAll_Click
        AddHandler Me.btnLibViwFilter.Click, AddressOf btnLibViwFilter_Click
        AddHandler Me.trvLibViwEndpoints.NodeMouseClick, AddressOf trvLibViwEndpoints_NodeMouseClick
        AddHandler Me.trvLibViwEndpoints.HandleCreated, AddressOf trvLibViwEndpoints_HandleCreated
        AddHandler Me.btnLibPthTemplateNew.Click, AddressOf btnLibPthTemplateNew_Click
        AddHandler Me.btnLibPthTemplateDelete.Click, AddressOf btnLibPthTemplateDelete_Click
        AddHandler Me.btnLibPthTemplateUp.Click, AddressOf btnLibPthTemplateUp_Click
        AddHandler Me.btnLibPthTemplateDown.Click, AddressOf btnLibPthTemplateDown_Click
        AddHandler Me.lstLibPthTemplate.SelectedIndexChanged, AddressOf lstLibPthTemplate_SelectedIndexChanged
        ' Owner-draw the template list so each category (Standard / Radio / Podcast) gets a band
        ' header above its first template. IntegralHeight off so variable-height rows aren't clipped.
        Me.lstLibPthTemplate.IntegralHeight = False
        Me.lstLibPthTemplate.DrawMode = System.Windows.Forms.DrawMode.OwnerDrawVariable
        AddHandler Me.lstLibPthTemplate.MeasureItem, AddressOf lstLibPthTemplate_MeasureItem
        AddHandler Me.lstLibPthTemplate.DrawItem, AddressOf lstLibPthTemplate_DrawItem
        AddHandler Me.txtLibPthName.TextChanged, AddressOf pathsName_TextChanged
        AddHandler Me.lstLibPthList.SelectedIndexChanged, AddressOf lstLibPthList_SelectedIndexChanged
        AddHandler Me.btnLibPthAdd.Click, AddressOf btnLibPthAdd_Click
        AddHandler Me.btnLibPthRemove.Click, AddressOf btnLibPthRemove_Click
        AddHandler Me.btnLibPthUp.Click, AddressOf btnLibPthUp_Click
        AddHandler Me.btnLibPthDown.Click, AddressOf btnLibPthDown_Click
        For Each picker As Plugin.FieldPickerCombo In {Me.fpcLibPthField1, Me.fpcLibPthField2, Me.fpcLibPthField3, Me.fpcLibPthAlbum1, Me.fpcLibPthAlbum2}
            AddHandler picker.FieldPicked, AddressOf viewsField_SelectedIndexChanged
        Next
        AddHandler Me.chkLibPthField1Bucket.CheckedChanged, AddressOf viewsPathEditor_Changed
        AddHandler Me.chkLibPthField2Bucket.CheckedChanged, AddressOf viewsPathEditor_Changed
        AddHandler Me.chkLibPthField3Bucket.CheckedChanged, AddressOf viewsPathEditor_Changed
        AddHandler Me.btnLibPthField1Sort.Click, AddressOf viewsFieldSort_Click
        AddHandler Me.btnLibPthField2Sort.Click, AddressOf viewsFieldSort_Click
        AddHandler Me.btnLibPthField3Sort.Click, AddressOf viewsFieldSort_Click
        AddHandler Me.btnLibPthAlbum1Sort.Click, AddressOf viewsFieldSort_Click
        AddHandler Me.btnLibPthAlbum2Sort.Click, AddressOf viewsFieldSort_Click
        AddHandler Me.radLibPthLeafAT.CheckedChanged, AddressOf viewsLeaf_CheckedChanged
        AddHandler Me.radLibPthLeafT.CheckedChanged, AddressOf viewsLeaf_CheckedChanged
        AddHandler Me.chkLibPthIncludeAllTracks.CheckedChanged, AddressOf viewsPathEditor_Changed
        AddHandler Me.btnLibViwApply.Click, AddressOf btnLibViwApply_Click
        AddHandler Me.chkLibViwVisible.Click, AddressOf chkLibViwVisible_Click
        AddHandler Me.chkLibViwTopLevel.Click, AddressOf chkLibViwTopLevel_Click
        AddHandler Me.tbcLibraryPaths.SelectedIndexChanged, AddressOf tbcLibraryPaths_SelectedIndexChanged
        AddHandler Me.chkDevForceNativeStream.CheckedChanged, AddressOf chkDevForceNativeStream_CheckedChanged
        AddHandler Me.chkDevForceTranscoding.CheckedChanged, AddressOf chkDevForceTranscoding_CheckedChanged
        AddHandler Me.btnLibOptHierAdd.Click, AddressOf libOptHierAdd_Click
        AddHandler Me.btnLibOptHierRemove.Click, AddressOf libOptHierRemove_Click
        AddHandler Me.lstLibOptHierFields.SelectedIndexChanged, AddressOf libOptHierList_SelectionChanged
        ' --- ToolTips (instance-stored when we need to update them later) ---
        Dim maxConnTip As New System.Windows.Forms.ToolTip() With {.AutoPopDelay = 15000, .InitialDelay = 400, .ReshowDelay = 200}
        maxConnTip.SetToolTip(Me.lblSrvMaxConnections, Plugin.L("MaxConnectionsTip"))
        maxConnTip.SetToolTip(Me.numSrvMaxConnections, Plugin.L("MaxConnectionsTip"))
        Dim pathsActionTip As New System.Windows.Forms.ToolTip()
        pathsActionTip.SetToolTip(Me.btnLibPthTemplateNew, Plugin.L("PathsNewTooltip"))
        pathsActionTip.SetToolTip(Me.btnLibPthTemplateDelete, Plugin.L("PathsDeleteTooltip"))
        ' --- Icon buttons (icon-only, label moved to a tooltip) ---
        Dim icons As New System.Resources.ResourceManager("MusicBeePlugin.Images", System.Reflection.Assembly.GetExecutingAssembly())
        Dim viewsActionTip As New System.Windows.Forms.ToolTip()
        Me.btnLibViwExpandAll.Text = ""
        Me.btnLibViwExpandAll.Image = CType(icons.GetObject("icon-lucide-list-chevrons-up-down"), System.Drawing.Image)
        viewsActionTip.SetToolTip(Me.btnLibViwExpandAll, Plugin.L("btnLibViwExpandAll"))
        Me.btnLibViwCollapseAll.Text = ""
        Me.btnLibViwCollapseAll.Image = CType(icons.GetObject("icon-lucide-list-chevrons-down-up"), System.Drawing.Image)
        viewsActionTip.SetToolTip(Me.btnLibViwCollapseAll, Plugin.L("btnLibViwCollapseAll"))
        Me.btnLibViwFilter.Text = ""
        Me.btnLibViwFilter.Image = CType(icons.GetObject("icon-lucide-funnel"), System.Drawing.Image)
        viewsActionTip.SetToolTip(Me.btnLibViwFilter, Plugin.L("btnLibViwFilter"))
        ' Field-sort toggles: icon-only, state held in .Tag (True = descending Z→A). See SetSortButton/IsSortDescending.
        sortIconAsc = CType(icons.GetObject("icon-lucide-arrow-down-a-z"), System.Drawing.Image)
        sortIconDesc = CType(icons.GetObject("icon-lucide-arrow-down-z-a"), System.Drawing.Image)
        For Each sb As System.Windows.Forms.Button In {Me.btnLibPthField1Sort, Me.btnLibPthField2Sort, Me.btnLibPthField3Sort, Me.btnLibPthAlbum1Sort, Me.btnLibPthAlbum2Sort}
            sb.Text = ""
            SetSortButton(sb, False)
            viewsActionTip.SetToolTip(sb, Plugin.L("ViewsSortToggleTip"))
        Next
        ' Reorder buttons (move selected item up/down) - path list + template list.
        Dim upIcon As System.Drawing.Image = CType(icons.GetObject("icon-lucide-chevron-up"), System.Drawing.Image)
        Dim downIcon As System.Drawing.Image = CType(icons.GetObject("icon-lucide-chevron-down"), System.Drawing.Image)
        For Each ub As System.Windows.Forms.Button In {Me.btnLibPthUp, Me.btnLibPthTemplateUp}
            ub.Text = ""
            ub.Image = upIcon
            pathsActionTip.SetToolTip(ub, Plugin.L("MoveUpTip"))
        Next
        For Each db As System.Windows.Forms.Button In {Me.btnLibPthDown, Me.btnLibPthTemplateDown}
            db.Text = ""
            db.Image = downIcon
            pathsActionTip.SetToolTip(db, Plugin.L("MoveDownTip"))
        Next
        ' Add / remove buttons - path list + template list (tooltips for the template pair set above).
        Dim addIcon As System.Drawing.Image = CType(icons.GetObject("icon-lucide-circle-plus"), System.Drawing.Image)
        Dim removeIcon As System.Drawing.Image = CType(icons.GetObject("icon-lucide-circle-minus"), System.Drawing.Image)
        Me.btnLibPthAdd.Text = ""
        Me.btnLibPthAdd.Image = addIcon
        pathsActionTip.SetToolTip(Me.btnLibPthAdd, Plugin.L("btnLibPthAdd"))
        Me.btnLibPthRemove.Text = ""
        Me.btnLibPthRemove.Image = removeIcon
        pathsActionTip.SetToolTip(Me.btnLibPthRemove, Plugin.L("btnLibPthRemove"))
        Me.btnLibPthTemplateNew.Text = ""
        Me.btnLibPthTemplateNew.Image = addIcon
        Me.btnLibPthTemplateDelete.Text = ""
        Me.btnLibPthTemplateDelete.Image = removeIcon
        ' Footer buttons: both icon-only 28x28 (the UI standard). Help's label moves to its
        ' tooltip; GitHub is a brand button ("GitHub" left unwired, like other brand strings).
        Me.btnHelp.Text = ""
        Me.btnHelp.Image = CType(icons.GetObject("icon-lucide-circle-question-mark"), System.Drawing.Image)
        viewsActionTip.SetToolTip(Me.btnHelp, Plugin.L("btnHelp"))
        Me.btnGithub.Text = ""
        Me.btnGithub.Image = CType(icons.GetObject("icon-yaiol-github"), System.Drawing.Image)
        viewsActionTip.SetToolTip(Me.btnGithub, "GitHub")
        viewsBucketTip = New System.Windows.Forms.ToolTip() With {.AutoPopDelay = 8000, .InitialDelay = 400, .ReshowDelay = 200}
        Dim badgeTip As New System.Windows.Forms.ToolTip()
        badgeTip.SetToolTip(Me.lblMaxConnectionsBadge, Plugin.L("MaxConnectionsBadgeTip"))
        badgeTip.SetToolTip(Me.lblRestartRequiredBadge, Plugin.L("RestartRequiredBadgeTip"))
        ' --- Visibility driven by runtime Plugin state ---
        Me.lblMaxConnectionsBadge.Visible = Plugin.MaxConnectionsHit
        Me.lblRestartRequiredBadge.Visible = Plugin.RestartRequired
        ' --- Bold "Apply" button on the UPnP View tab ---
        Me.btnLibViwApply.Font = New System.Drawing.Font(Me.Font, System.Drawing.FontStyle.Bold)
        ' --- Initial per-sub-tab button-block visibility (Paths sub-tab is selected at startup) ---
        UpdateLibraryPathsTabButtons()
    End Sub

    ' ToolTip shared by the 3 bucket checkboxes - created in ApplyDesignerExtras, mutated at runtime
    ' as the user changes the Group By dropdowns (so the tip reads e.g. "Split [Album Artist] by...").
    Private viewsBucketTip As System.Windows.Forms.ToolTip
    ' Working state for the Views editor: a direct reference to the currently-selected live
    ' template. The user mutates it via the right-side controls and the edits land immediately
    ' (no Save step); the dialog's Save persists the whole templates list to disk.
    Private viewsEditingTemplate As Plugin.BrowseTemplate = Nothing
    ' Guard to suppress side-effects while we programmatically update controls (avoids
    ' triggering "user changed something" handlers during a Load).
    Private viewsSuppressEvents As Boolean = False

    Private Sub tbcProfiles_SelectedIndexChanged(sender As Object, e As EventArgs)

    End Sub

    Private Sub cboDevTranscodeSampleRate_SelectedIndexChanged(sender As Object, e As EventArgs)

    End Sub

    Private Sub cboDevTranscodeFormat_SelectedIndexChanged(sender As Object, e As EventArgs)

    End Sub
End Class
