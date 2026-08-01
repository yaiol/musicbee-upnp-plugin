Imports System.Text
Imports System.IO
Imports System.Threading
Imports System.Xml
Imports System.Xml.Linq
Imports System.Collections.ObjectModel

Partial Public Class Plugin
    Friend Class ItemManager
        Public SupportedMimeTypes() As String = Nothing
        Public DisablePcmTimeSeek As Boolean = False
        Private ReadOnly template As New TemplateNode(Nothing, Nothing, Nothing, Nothing, "object.container", Nothing)
        Private treeIsLoaded As Boolean = False
        Private tree As New FolderNode
        Private ReadOnly streamingProfile As StreamingProfile
        Private ReadOnly encodeSettings As New MediaSettings
        Private Shared musicFiles As List(Of String())
        Private Shared radioFiles As List(Of String())
        Private Shared audiobookFiles As List(Of String())
        Private Shared inboxFiles As List(Of String())
        Private Shared podcastFiles As List(Of String())
        ' yaiol - fileId → MB Podcasts subscription id. Built by LoadPodcastFiles. Lets
        ' the lazy DIDL emitter route album artwork through Podcasts_GetSubscriptionArt-
        ' work (the only artwork source MB exposes for podcast subscriptions) instead of
        ' Library_GetArtworkUrl which returns "0" for synthesized podcast episode urls.
        Friend Shared ReadOnly podcastSubIdByFileId As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ' Subscription Album-name → subId. Podcast albums are emitted at the
        ' ValueDistinct level (the user's binding groups by Album as a regular hierarchy
        ' step, not as the AT leaf), which means the album container goes through
        ' WriteLazyContainer with artworkSourceUrl=Nothing - the fileId-keyed path can't
        ' help. Match by Album title (= subscription name) instead. 1:1 lookup so case-
        ' insensitive direct lookup is fine.
        Friend Shared ReadOnly podcastSubIdByAlbumName As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ' subId → feed url (subInfo[0]) - used by GetPodcastThumbnail when Podcasts_-
        ' GetSubscriptionArtwork doesn't yield bytes (this MB build silently returns
        ' False on that call even when MusicBee itself displays the artwork - see the
        ' fallback chain in the HTTP handler).
        Friend Shared ReadOnly podcastFeedUrlBySubId As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ' subId → on-disk subscription folder, derived from any episode URL's parent
        ' directory. Used to find a `folder.jpg` / `cover.jpg` next to the downloaded
        ' episodes when the MB API (Podcasts_GetSubscriptionArtwork) doesn't return
        ' bytes - empirically that API is half-implemented in this MB build and
        ' returns False even when the desktop UI shows the artwork.
        '
        ' yaiol convention (not MB-mandated): for each podcast subscription the user
        ' drops a `folder.jpg` (or `cover.jpg`, `folder.png`, …) inside the override
        ' download folder configured on the MB subscription dialog. The on-disk path
        ' is `<override folder>\folder.jpg`, e.g.
        '   M:\Download\Podcast\History\Franck Ferrand raconte\folder.jpg
        ' If you maintain podcast subscriptions outside MusicBee, drop a folder.jpg
        ' here too and BubbleUPnP picks it up via /PodcastThumbnail/<subId>.
        Friend Shared ReadOnly podcastFolderBySubId As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ' subId → subscription display name. The HTTP handler uses this to find
        ' MB's own cached artwork at %LocalAppData%\MusicBee\InternalCache\
        ' Subscriptions\<subName>.jpg - that's where MB stores the JPEG it
        ' shows in its desktop UI for each subscription. This is the most
        ' reliable artwork source on this MB build, since Podcasts_-
        ' GetSubscriptionArtwork returns False and the user's download folders
        ' rarely have a hand-dropped folder.jpg.
        Friend Shared ReadOnly podcastSubNameBySubId As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ' slug → real subscription id. Slug is the lowercase last path segment of
        ' the subId, used as the URL path component for /PodcastThumbnail/<slug>.
        ' Required because the HTTP server unescapes and lowercases incoming URLs
        ' (HttpRequest.ParseHeaders), which mangles the real subId if it's a URL
        ' (e.g. "https://feeds.audiomeans.fr/feed/abc.xml" becomes "abc.xml" after
        ' the route handler's LastIndexOf("/")). The slug round-trips cleanly.
        Friend Shared ReadOnly podcastSubIdBySlug As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Friend Shared Function PodcastSlug(subId As String) As String
            If String.IsNullOrEmpty(subId) Then Return subId
            Dim idx As Integer = subId.LastIndexOfAny(New Char() {"/"c, "\"c})
            Dim seg As String = If(idx >= 0, subId.Substring(idx + 1), subId)
            Return seg.ToLowerInvariant()
        End Function
        Private Shared playlistRootStandard As FolderNode
        Private Shared playlistRootWmcCompat As FolderNode
        Private Shared filterFolderNodes As List(Of FolderNode)
        Private Shared filterTemplateNodes As List(Of TemplateNode)
        Private Shared ReadOnly fileLookup As New Dictionary(Of String, String())(StringComparer.OrdinalIgnoreCase)
        Private Shared ReadOnly itemManagerLookup As New Dictionary(Of String, ItemManager)(StringComparer.OrdinalIgnoreCase)
        Private Shared libraryIsDirty As Boolean = False
        Private Shared fileLookupLoaded As Boolean = False
        ' Search-side album index. Built lazily by BuildSearchAlbumIndex when a UPnP client
        ' (e.g. BubbleUPnP's "Random Albums") issues a Search targeting musicAlbum class.
        ' searchAlbumKeys(i) = "<AlbumArtist>|<Album>" - stable key, gives ID "Salb<i>".
        ' searchAlbumTracks(key) = list of tracks for that album, sorted disc/track.
        ' Rebuilt whenever musicFiles is reloaded (gated by searchAlbumsBuiltCount mismatch).
        Private Shared searchAlbumKeys As New List(Of String)
        Private Shared searchAlbumTracks As New Dictionary(Of String, List(Of String()))(StringComparer.OrdinalIgnoreCase)
        Private Shared searchAlbumsBuiltCount As Integer = -1
        ' Lazy-search album-result cache. Maps the synthetic container ID we emit in
        ' search responses (e.g. "Ssrch_alb_2") → the list of tracks for that album.
        ' Browse uses this to respond when the user taps an album from search results.
        ' Without it the album appears in results but clicking it returns empty (the
        ' generic Browse path can't decode "Ssrch_alb_N" and falls to "(not matched)").
        ' Bounded by the per-search cap (currently 10 slots), so memory growth is nil.
        Private Shared ReadOnly searchAlbumResultTracks As New Dictionary(Of String, List(Of String()))(StringComparer.OrdinalIgnoreCase)
        ' Positional list of fields Library_GetFileTags pre-loads per track. Order MUST match
        ' the MetaDataIndex enum exactly. Positive values are MetaDataType; negative values are
        ' FilePropertyType (MusicBee distinguishes them by sign). Position 25 (the literal 0)
        ' is AlbumArtistAndAlbum - a derived field the plugin synthesises later, no MusicBee
        ' source. The block past SortAlbumArtist (position 26) is the extension introduced for
        ' the Fields tab.
        Private Shared ReadOnly queryFields() As Plugin.MetaDataType = DirectCast(New Integer() {
            MetaDataType.Url, MetaDataType.Category, MetaDataType.Artist, MetaDataType.ArtistPeople, MetaDataType.AlbumArtist, MetaDataType.Composer, MetaDataType.Conductor, MetaDataType.TrackTitle, MetaDataType.Album, MetaDataType.TrackNo, MetaDataType.DiscNo, MetaDataType.DiscCount, MetaDataType.Year, MetaDataType.Genre, MetaDataType.Publisher, MetaDataType.Rating, -MetaDataType.Duration, -MetaDataType.FileSize, -MetaDataType.Bitrate, -MetaDataType.SampleRate, -MetaDataType.Channels, -MetaDataType.DateAdded, -MetaDataType.PlayCount, -MetaDataType.DateLastPlayed, -MetaDataType.ReplayGainTrack, 0, MetaDataType.SortAlbumArtist,
            MetaDataType.SortArtist, MetaDataType.SortComposer, MetaDataType.SortAlbum,
            MetaDataType.Mood, MetaDataType.Grouping, MetaDataType.Work, MetaDataType.Language, MetaDataType.Occasion, MetaDataType.Origin, MetaDataType.OriginalArtist, MetaDataType.OriginalYear,
            MetaDataType.Custom1, MetaDataType.Custom2, MetaDataType.Custom3, MetaDataType.Custom4, MetaDataType.Custom5, MetaDataType.Custom6, MetaDataType.Custom7, MetaDataType.Custom8, MetaDataType.Custom9, MetaDataType.Custom10, MetaDataType.Custom11, MetaDataType.Custom12, MetaDataType.Custom13, MetaDataType.Custom14, MetaDataType.Custom15, MetaDataType.Custom16,
            MetaDataType.Virtual1, MetaDataType.Virtual2, MetaDataType.Virtual3, MetaDataType.Virtual4, MetaDataType.Virtual5, MetaDataType.Virtual6, MetaDataType.Virtual7, MetaDataType.Virtual8, MetaDataType.Virtual9, MetaDataType.Virtual10, MetaDataType.Virtual11, MetaDataType.Virtual12, MetaDataType.Virtual13, MetaDataType.Virtual14, MetaDataType.Virtual15, MetaDataType.Virtual16, MetaDataType.Virtual17, MetaDataType.Virtual18, MetaDataType.Virtual19, MetaDataType.Virtual20, MetaDataType.Virtual21, MetaDataType.Virtual22, MetaDataType.Virtual23, MetaDataType.Virtual24, MetaDataType.Virtual25
        }, Plugin.MetaDataType())
        Private Shared ReadOnly musicCategory As String = mbApiInterface.MB_GetLocalisation("Main.tree.Music", "Music")
        Private Shared ReadOnly radioCategory As String = mbApiInterface.MB_GetLocalisation("Main.tree.RaSt", "Radio")
        Private Shared ReadOnly audiobookCategory As String = mbApiInterface.MB_GetLocalisation("Main.tree.AuBo", "Audiobooks")
        Private Shared ReadOnly inboxCategory As String = mbApiInterface.MB_GetLocalisation("Main.tree.Inbox", "Inbox")
        Private Shared ReadOnly playlistsCategory As String = mbApiInterface.MB_GetLocalisation("Main.tree.Playlists", "Playlists")

        Public Shared Function GetItemManager(requestHeaders As Dictionary(Of String, String)) As ItemManager
            Dim profile As StreamingProfile = Settings.GetStreamingProfile(requestHeaders)
            SyncLock itemManagerLookup
                Dim itemManager As ItemManager
                If Not itemManagerLookup.TryGetValue(profile.ProfileName, itemManager) Then
                    itemManager = New ItemManager(profile)
                    itemManagerLookup.Add(profile.ProfileName, itemManager)
                End If
                Return itemManager
            End SyncLock
        End Function

        Private Sub New(profile As StreamingProfile)
            streamingProfile = profile
        End Sub

        Public Shared Sub SetLibraryDirty()
            ' Tell UPnP clients to invalidate their DIDL caches. Without this bump,
            ' clients keep showing pre-mutation content until they happen to refresh
            ' for some other reason. See ContentDirectoryService.systemUpdateId for
            ' the why.
            ContentDirectoryService.BumpSystemUpdateId()
            SyncLock fileLookup
                libraryIsDirty = True
                ' Lazy caches were built from the previous tag state - drop them so the
                ' next Browse re-fetches via Library_GetFileTag/QueryFilesEx and reflects
                ' whatever the user just changed (rating, play count, tag edit, file add/
                ' remove). Cheap to rebuild on demand because each level filter narrows the
                ' working set to ~one artist's worth of tracks.
                lazyDistinctCache.Clear()
                lazyAlbumsCache.Clear()
                lazyResourceUrls.Clear()
                lazyFilterConditionsCache.Clear()
                ' Same reason: a cached search result set was matched against the old tags.
                ' (Safe lock order - GetCachedSearchUrls never holds this lock while querying,
                ' so it can't deadlock against the fileLookup lock held here.)
                SyncLock lazySearchUrlCache
                    lazySearchUrlCache.Clear()
                End SyncLock
                ' Drop in-memory endpoint loaded flags so audiobook/inbox/radio/podcast
                ' get re-fetched on next browse to reflect tag/file mutations.
                audiobookFilesLoaded = False
                inboxFilesLoaded = False
                radioFilesLoaded = False
                podcastFilesLoaded = False
                domainsPartitioned = False
            End SyncLock
        End Sub

        ' yaiol - fully drop cached state so the next browse request rebuilds the tree from disk.
        ' Used when settings change the *structure* of the tree (hidden filters, etc.) - flagging
        ' dirty alone only refreshes file lists, not the tree assembly which is built once per
        ' ItemManager instance.
        Public Shared Sub ResetCache()
            ' Settings changes (new template, endpoint binding change, view edit) reshape
            ' the browse tree itself - container IDs the client has cached now point at
            ' paths that may no longer exist. Bump SystemUpdateID so UPnP clients see the
            ' UpdateID change on their next Browse and re-fetch DIDL. Without this,
            ' BubbleUPnP keeps serving stale DIDL from its cache until some opaque
            ' trigger forces it to refresh.
            ContentDirectoryService.BumpSystemUpdateId()
            SyncLock itemManagerLookup
                itemManagerLookup.Clear()
            End SyncLock
            SyncLock fileLookup
                libraryIsDirty = True
                fileLookupLoaded = False
                filterFolderNodes = Nothing
                filterTemplateNodes = Nothing
            End SyncLock
        End Sub

        Private Sub LoadLibrary()
            SyncLock fileLookup
                Dim loadRequired As Boolean
                If fileLookupLoaded Then
                    loadRequired = libraryIsDirty
                    libraryIsDirty = False
                Else
                    libraryIsDirty = False
                    loadRequired = True
                    fileLookupLoaded = True
                End If
                ' DIAG/TRANSITIONAL: eager loading entirely disabled. We don't fetch
                ' filenames, don't run the per-file GetFileTags loop, don't load
                ' radio/podcast/playlist/filter sets, don't build any endpoint tree.
                ' The only thing that survives is the lazy Music container below.
                ' Verify cold-start is essentially instant; from there we decide which
                ' endpoints to bring back as lazy implementations.
                If loadRequired Then
                    musicFiles = New List(Of String())
                    audiobookFiles = New List(Of String())
                    inboxFiles = New List(Of String())
                    radioFiles = New List(Of String())
                    podcastFiles = New List(Of String())
                End If
                If Not treeIsLoaded Then
                    treeIsLoaded = True
                    ' DIAG/TRANSITIONAL: every eager endpoint (Music + Audiobooks + Inbox +
                    ' Radio + Podcasts + Filters + Playlists + Now Playing) is OFF. Root
                    ' exposes only the lazy Music container so we can prove cold-start time
                    ' is ~0 with nothing in the eager path. Each endpoint will be
                    ' re-introduced as a lazy implementation one at a time once Music's
                    ' end-to-end behaviour is confirmed.
                    Dim rootChildren As New List(Of TemplateNode)
                    Dim rootFolders As New List(Of FolderNode)
                    ' Root order mirrors MusicBee's own left-pane order:
                    '   Music, Filters, Podcasts, Audiobooks, Radio, Inbox, Playlists.
                    ' Each endpoint is gated on its View binding's Exposed flag; lazy
                    ' endpoints (audiobook/inbox/radio/podcast) fetch their data into
                    ' an in-memory list only on first browse (EnsureLazyEndpointInMemory)
                    ' so cold-start stays cheap.
                    Dim musicBinding As EndpointBinding = View.GetBinding("music")
                    If musicBinding Is Nothing OrElse musicBinding.Exposed Then
                        Dim lazyMusicTree As New TemplateNode("L:music", "0", "L:music", musicCategory, "object.container", Nothing)
                        Dim lazyMusicFolder As New FolderNode(musicCategory)
                        rootChildren.Add(lazyMusicTree)
                        rootFolders.Add(lazyMusicFolder)
                    End If
                    ' Lazy Filters wrapper. Children are enumerated on demand from disk.
                    Dim lazyFiltersTree As New TemplateNode("L:filters", "0", "L:filters", Plugin.L("FiltersFolderName"), "object.container", Nothing)
                    Dim lazyFiltersFolder As New FolderNode(Plugin.L("FiltersFolderName"))
                    rootChildren.Add(lazyFiltersTree)
                    rootFolders.Add(lazyFiltersFolder)
                    ' Pinned filters are inserted here (directly below the Filters wrapper), not
                    ' appended at the end of root - see the pin loop below.
                    Dim filtersInsertAt As Integer = rootChildren.Count
                    Dim podcastBinding As EndpointBinding = View.GetBinding("podcast")
                    If podcastBinding Is Nothing OrElse podcastBinding.Exposed Then
                        Dim podcastLabel As String = mbApiInterface.MB_GetLocalisation("Main.tree.Podc", "Podcasts")
                        rootChildren.Add(New TemplateNode("L:podcast", "0", "L:podcast", podcastLabel, "object.container", Nothing))
                        rootFolders.Add(New FolderNode(podcastLabel))
                    End If
                    Dim audiobookBinding As EndpointBinding = View.GetBinding("audiobook")
                    If audiobookBinding Is Nothing OrElse audiobookBinding.Exposed Then
                        Dim audiobookLabel As String = audiobookCategory
                        rootChildren.Add(New TemplateNode("L:audiobook", "0", "L:audiobook", audiobookLabel, "object.container", Nothing))
                        rootFolders.Add(New FolderNode(audiobookLabel))
                    End If
                    Dim radioBinding As EndpointBinding = View.GetBinding("radio")
                    If radioBinding Is Nothing OrElse radioBinding.Exposed Then
                        Dim radioLabel As String = radioCategory
                        rootChildren.Add(New TemplateNode("L:radio", "0", "L:radio", radioLabel, "object.container", Nothing))
                        rootFolders.Add(New FolderNode(radioLabel))
                    End If
                    Dim inboxBinding As EndpointBinding = View.GetBinding("inbox")
                    If inboxBinding Is Nothing OrElse inboxBinding.Exposed Then
                        Dim inboxLabel As String = inboxCategory
                        rootChildren.Add(New TemplateNode("L:inbox", "0", "L:inbox", inboxLabel, "object.container", Nothing))
                        rootFolders.Add(new FolderNode(inboxLabel))
                    End If
                    ' Lazy Playlists wrapper. Children are enumerated on demand from MB.
                    Dim lazyPlaylistsTree As New TemplateNode("L:playlists", "0", "L:playlists", Plugin.L("PlaylistsFolderName"), "object.container", Nothing)
                    Dim lazyPlaylistsFolder As New FolderNode(Plugin.L("PlaylistsFolderName"))
                    rootChildren.Add(lazyPlaylistsTree)
                    rootFolders.Add(lazyPlaylistsFolder)
                    ' Pinned playlists are inserted here (directly below the Playlists wrapper).
                    Dim playlistsInsertAt As Integer = rootChildren.Count
                    ' yaiol - endpoints pinned to the browse root (binding.AtTopLevel). The pin
                    ' is ADDITIVE: the endpoint still appears in its natural wrapper AND here at
                    ' root. Each pin resolves to the SAME lazy container id its wrapper child
                    ' would use, so browsing the pinned copy reuses the existing handler. Built-in
                    ' endpoints already sit at root, so only filters/playlists pin meaningfully.
                    ' Each pin is grouped under its own category (filter pins below the Filters
                    ' wrapper, playlist pins below the Playlists wrapper) rather than trailing the
                    ' whole root, so a pinned endpoint sits next to its kind. Collect per category,
                    ' then splice in at the captured insert positions.
                    Dim pinnedFilterChildren As New List(Of TemplateNode)
                    Dim pinnedFilterFolders As New List(Of FolderNode)
                    Dim pinnedPlaylistChildren As New List(Of TemplateNode)
                    Dim pinnedPlaylistFolders As New List(Of FolderNode)
                    Dim pinnedPlUrlByName As Dictionary(Of String, String) = Nothing
                    For Each pb As EndpointBinding In View.EndpointBindings
                        If Not (pb.AtTopLevel AndAlso pb.Exposed) Then Continue For
                        Dim pinId As String = Nothing
                        Dim pinLabel As String = Nothing
                        Dim isFilterPin As Boolean = False
                        If pb.EndpointId.StartsWith("filter:", StringComparison.Ordinal) Then
                            Dim fname As String = pb.EndpointId.Substring("filter:".Length)
                            pinId = "L:filter:" & Uri.EscapeDataString(fname)
                            ' WYSIWYG prefix at root (verbatim, no auto-space) so pinned filters
                            ' stay distinguishable from their in-wrapper copy and group together.
                            pinLabel = If(Settings.FilterPrefix, "") & fname
                            isFilterPin = True
                        ElseIf pb.EndpointId.StartsWith("playlist:", StringComparison.Ordinal) Then
                            Dim pname As String = pb.EndpointId.Substring("playlist:".Length)
                            If pinnedPlUrlByName Is Nothing Then
                                pinnedPlUrlByName = New Dictionary(Of String, String)(StringComparer.Ordinal)
                                For Each e As LazyResourceEntry In EnumerateLazyPlaylists()
                                    pinnedPlUrlByName(e.Name) = e.Id
                                Next
                            End If
                            Dim plUrl As String = Nothing
                            If pinnedPlUrlByName.TryGetValue(pname, plUrl) Then
                                pinId = "L:playlist:" & Uri.EscapeDataString(plUrl)
                                Dim parts() As String = pname.Split("\"c)
                                pinLabel = If(Settings.PlaylistPrefix, "") & parts(parts.Length - 1)
                            End If
                        End If
                        ' Built-ins already at root (pinId Nothing) → nothing to add.
                        If pinId Is Nothing Then Continue For
                        If isFilterPin Then
                            pinnedFilterChildren.Add(New TemplateNode(pinId, "0", pinId, pinLabel, "object.container", Nothing))
                            pinnedFilterFolders.Add(New FolderNode(pinLabel))
                        Else
                            pinnedPlaylistChildren.Add(New TemplateNode(pinId, "0", pinId, pinLabel, "object.container", Nothing))
                            pinnedPlaylistFolders.Add(New FolderNode(pinLabel))
                        End If
                    Next
                    ' Splice the highest insert position first so the earlier index stays valid.
                    If pinnedPlaylistChildren.Count > 0 Then
                        rootChildren.InsertRange(playlistsInsertAt, pinnedPlaylistChildren)
                        rootFolders.InsertRange(playlistsInsertAt, pinnedPlaylistFolders)
                    End If
                    If pinnedFilterChildren.Count > 0 Then
                        rootChildren.InsertRange(filtersInsertAt, pinnedFilterChildren)
                        rootFolders.InsertRange(filtersInsertAt, pinnedFilterFolders)
                    End If
                    template.ChildNodes = rootChildren.ToArray()
                    tree.Folders = rootFolders.ToArray()
                End If
            End SyncLock
        End Sub


        Private Shared radioTagsProbed As Boolean = False

        ' Radio loader - was inline in old LoadLibrary. Dedupes by URL (MusicBee can return
        ' the same station twice for various reasons), tolerates GetFileTags failures, sorts
        ' by Title for stable paginated browsing (see history note in the Radio Browse branch).
        Private Shared Sub LoadRadioFiles()
            If radioFiles Is Nothing Then
                radioFiles = New List(Of String())
            Else
                radioFiles.Clear()
            End If
            Dim radioUrls() As String = Nothing
            mbApiInterface.Library_QueryFilesEx("domain=Radio", radioUrls)
            If radioUrls Is Nothing Then Return
            Dim seenRadioUrls As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each url As String In radioUrls
                If Not seenRadioUrls.Add(url) Then Continue For
                Dim tags() As String = Nothing
                Try
                    mbApiInterface.Library_GetFileTags(url, queryFields, tags)
                Catch ex As Exception
                    LogError(ex, "LoadRadioFiles.GetFileTags", "url=" & url)
                    Continue For
                End Try
                If tags Is Nothing OrElse tags.Length <= CInt(MetaDataIndex.Url) Then Continue For
                EnsureTagSlots(tags)
                ' MusicBee stores the radio "folder" attribute (shown in the station edit
                ' dialog) in the Category slot. Copy it to the synthetic ExtraField1 so
                ' the "MusicBee Folder" grouping field works uniformly for Radio + Podcasts.
                tags(MetaDataIndex.ExtraField1) = tags(MetaDataIndex.Category)
                ' One-shot probe: dump every MetaDataIndex slot with its name + value for
                ' the first radio entry so we can see which slot MusicBee fills with the
                ' radio "folder" (or whatever organisational field we want to map). Writes
                ' unconditionally to RadioProbe.txt next to UpnpErrorLog.dat.
                If Not radioTagsProbed Then
                    radioTagsProbed = True
                    Try
                        Dim probePath As String = mbApiInterface.Setting_GetPersistentStoragePath() & "RadioProbe.txt"
                        Using w As New IO.StreamWriter(probePath, False)
                            w.WriteLine("Radio first-entry tag probe - " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                            w.WriteLine("URL: " & url)
                            w.WriteLine("Tags array length (post-pad): " & tags.Length)
                            w.WriteLine("")
                            w.WriteLine("Position  Name                       Value")
                            w.WriteLine("--------  -------------------------  -----")
                            For Each idx As MetaDataIndex In [Enum].GetValues(GetType(MetaDataIndex))
                                If CInt(idx) < 0 Then Continue For
                                If CInt(idx) >= tags.Length Then Continue For
                                Dim n As String = [Enum].GetName(GetType(MetaDataIndex), idx)
                                Dim v As String = If(tags(CInt(idx)), "<null>")
                                w.WriteLine(CInt(idx).ToString().PadLeft(3) & "       " & n.PadRight(27) & """" & v & """")
                            Next
                        End Using
                    Catch ex As Exception
                        LogError(ex, "LoadRadioFiles.Probe")
                    End Try
                End If
                SanitizeTags(tags)
                If String.IsNullOrEmpty(tags(MetaDataIndex.Url)) Then Continue For
                Dim id As String = GetFileId(tags(MetaDataIndex.Url))
                If fileLookup.ContainsKey(id) Then
                    Dim attempt As Integer = 1
                    Dim newId As String = id
                    Do While fileLookup.ContainsKey(newId) AndAlso attempt < 100
                        newId = id.Substring(0, id.Length - 2) & attempt.ToString("X2")
                        attempt += 1
                    Loop
                    id = newId
                End If
                fileLookup.Add(id, tags)
                radioFiles.Add(tags)
            Next
            radioFiles.Sort(New TrackNameFileComparer)
            LogInformation("LazyQuery", "[LoadRadioFiles] domain=Radio finalCount=" & radioFiles.Count)
        End Sub

        ' Podcast loader. MusicBee's Podcasts_* API returns subscriptions and episodes as
        ' opaque String() tuples (subscription tuple ≈ [url, name, author, homePage,
        ' description, folder, …]; episode tuple ≈ [url, title, …]). Exact layout isn't
        ' documented and the dialog visual order is used as the best guess - the index
        ' constants below are ⚠ CHANGE-ME if the runtime data tells us otherwise.
        '
        ' Each episode is materialised into a queryFields-shaped tag array so it can flow
        ' through the same BuildEndpointTree / BuildHierarchicalFolderNode pipeline as any
        ' library track: subscription name → Album, subscription author → Artist +
        ' AlbumArtist, episode title → TrackTitle, episode URL → Url. The default
        ' "Podcasts" template groups by Album, which means each subscription becomes one
        ' folder containing its episodes.
        ' Confirmed via PodcastProbe.txt (2026-05-26) against real subscriptions:
        '   [0] feed URL    [1] subscription name    [2] MusicBee folder
        '   [3] iTunes category/breadcrumb    [4] description    [5] episode counter
        ' Episode tuple:
        '   [0] file URL    [1] title    [2] date    [3] description    [4] duration
        '   [5] downloaded? (bool string)    [6] played? (bool string)
        ' There is no native "author" slot, so the show itself is treated as the artist:
        ' the subscription name is mirrored into Artist/AlbumArtist (+ their Sort variants),
        ' same as it fills Album - otherwise any artist-based grouping path finds every
        ' artist slot empty and dead-ends to an empty level. See LoadPodcastFiles.
        Private Const PodcastSubNameIdx As Integer = 1
        Private Const PodcastSubFolderIdx As Integer = 2
        Private Const PodcastEpisodeUrlIdx As Integer = 0
        Private Const PodcastEpisodeTitleIdx As Integer = 1
        ' Episode tuple [2] is the publish date+time (e.g. "02/06/2026 15:03"). The only year
        ' data podcasts carry - parsed into the Year slot so podcast paths can group by year.
        Private Const PodcastEpisodeDateIdx As Integer = 2

        ' Duration (ticks) + FileSize (bytes) as RAW numerics - the negative field code tells
        ' Library_GetFileTags to return MB's unformatted value (same trick queryFields uses for
        ' Duration/FileSize). Fetched from a downloaded episode's real file so its DIDL can
        ' advertise a concrete res@duration/@size; without a duration the renderer (BubbleUPnP)
        ' re-runs a full-stream ffprobe metadata extraction on every play. Order MUST match the
        ' read order below (0 = Duration, 1 = FileSize).
        Private Shared ReadOnly podcastDurationSizeFields() As Plugin.MetaDataType = DirectCast(New Integer() {
            -MetaDataType.Duration, -MetaDataType.FileSize
        }, Plugin.MetaDataType())

        ' Pull the 4-digit year out of a podcast publish-date string. Tries a culture-aware date
        ' parse first (DD/MM vs MM/DD is irrelevant - the year is the same either way), then falls
        ' back to the first 4-digit run. Returns "" when no year can be found.
        Private Shared Function ExtractYear(s As String) As String
            If String.IsNullOrEmpty(s) Then Return ""
            Dim dt As DateTime
            If DateTime.TryParse(s, dt) Then Return dt.Year.ToString()
            Dim m As System.Text.RegularExpressions.Match = System.Text.RegularExpressions.Regex.Match(s, "\d{4}")
            If m.Success Then Return m.Value
            Return ""
        End Function

        Private Shared podcastTupleProbed As Boolean = False

        Private Shared Sub LoadPodcastFiles()
            If podcastFiles Is Nothing Then
                podcastFiles = New List(Of String())
            Else
                podcastFiles.Clear()
            End If
            podcastSubIdByFileId.Clear()
            podcastSubIdByAlbumName.Clear()
            podcastFeedUrlBySubId.Clear()
            podcastFolderBySubId.Clear()
            podcastSubNameBySubId.Clear()
            podcastSubIdBySlug.Clear()
            Dim subscriptionIds() As String = Nothing
            Try
                If Not mbApiInterface.Podcasts_QuerySubscriptions("", subscriptionIds) Then Return
            Catch ex As Exception
                LogError(ex, "LoadPodcastFiles.QuerySubscriptions")
                Return
            End Try
            If subscriptionIds Is Nothing Then Return
            ' One-shot diagnostic probe - dump the first subscription's tuple positions and
            ' the first episode's tuple positions so we can verify (and if needed correct)
            ' the PodcastSub*Idx / PodcastEpisode*Idx guesses. Fires once per plugin load.
            ' Writes directly to PodcastProbe.txt next to UpnpErrorLog.dat, bypassing the
            ' Settings.LogDebugInfo gate so the user doesn't have to enable verbose logging
            ' just to read out the tuple layout.
            If Not podcastTupleProbed Then
                podcastTupleProbed = True
                Try
                    Dim probePath As String = mbApiInterface.Setting_GetPersistentStoragePath() & "PodcastProbe.txt"
                    Using w As New IO.StreamWriter(probePath, False)
                        w.WriteLine("Podcast tuple probe - " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                        w.WriteLine("Subscriptions returned by Podcasts_QuerySubscriptions: " & subscriptionIds.Length)
                        w.WriteLine("Note: index 0 is often a synthetic filter (All / Unplayed Episodes).")
                        w.WriteLine("      Look for real subscriptions further down the list.")
                        w.WriteLine("")
                        For sIdx As Integer = 0 To subscriptionIds.Length - 1
                            w.WriteLine("======================================================================")
                            w.WriteLine("Subscription #" & sIdx & " - id: " & subscriptionIds(sIdx))
                            Dim probeSubInfo() As String = Nothing
                            If mbApiInterface.Podcasts_GetSubscription(subscriptionIds(sIdx), probeSubInfo) AndAlso probeSubInfo IsNot Nothing Then
                                w.WriteLine("  tuple length=" & probeSubInfo.Length)
                                For i As Integer = 0 To probeSubInfo.Length - 1
                                    w.WriteLine("  [" & i & "] = """ & If(probeSubInfo(i), "<null>") & """")
                                Next
                            Else
                                w.WriteLine("  Podcasts_GetSubscription returned False or null")
                            End If
                            ' First episode of this subscription, if any.
                            Dim probeEpUrls() As String = Nothing
                            If mbApiInterface.Podcasts_GetSubscriptionEpisodes(subscriptionIds(sIdx), probeEpUrls) AndAlso probeEpUrls IsNot Nothing AndAlso probeEpUrls.Length > 0 Then
                                w.WriteLine("  episodes=" & probeEpUrls.Length & ", first url=" & probeEpUrls(0))
                                Dim probeEpisode() As String = Nothing
                                If mbApiInterface.Podcasts_GetSubscriptionEpisode(subscriptionIds(sIdx), 0, probeEpisode) AndAlso probeEpisode IsNot Nothing Then
                                    w.WriteLine("  episode tuple length=" & probeEpisode.Length)
                                    For i As Integer = 0 To probeEpisode.Length - 1
                                        w.WriteLine("  ep[" & i & "] = """ & If(probeEpisode(i), "<null>") & """")
                                    Next
                                End If
                            Else
                                w.WriteLine("  episodes: none / not queryable")
                            End If
                            w.WriteLine("")
                        Next
                    End Using
                Catch ex As Exception
                    LogError(ex, "LoadPodcastFiles.Probe")
                End Try
            End If
            For Each subId As String In subscriptionIds
                Dim subInfo() As String = Nothing
                Try
                    If Not mbApiInterface.Podcasts_GetSubscription(subId, subInfo) Then Continue For
                Catch ex As Exception
                    LogError(ex, "LoadPodcastFiles.GetSubscription", "id=" & subId)
                    Continue For
                End Try
                Dim subName As String = If(subInfo IsNot Nothing AndAlso subInfo.Length > PodcastSubNameIdx, subInfo(PodcastSubNameIdx), subId)
                Dim subFolder As String = If(subInfo IsNot Nothing AndAlso subInfo.Length > PodcastSubFolderIdx, subInfo(PodcastSubFolderIdx), "")
                ' subInfo[0] is the feed URL per the probe-confirmed layout in the
                ' comment above. Remember it so the HTTP handler can fall back to
                ' Library_GetArtworkUrl(feedUrl, …) when Podcasts_GetSubscriptionArtwork
                ' returns False (which it does on this MB build, despite the desktop UI
                ' clearly having the artwork - see screenshot test).
                If subInfo IsNot Nothing AndAlso subInfo.Length > 0 AndAlso Not String.IsNullOrEmpty(subInfo(0)) Then
                    podcastFeedUrlBySubId(subId) = subInfo(0)
                End If
                ' Skip MB's synthetic / virtual / orphan entries - they don't represent a
                ' real podcast subscription:
                '   • "All" / "Recent"           - synthetic cross-cutting views
                '   • Empty id                   - "Unknown Subscription" orphan bucket
                '   • Empty name AND empty id    - defensive catch-all
                If String.IsNullOrEmpty(subId) Then Continue For
                If String.Equals(subId, "All", StringComparison.OrdinalIgnoreCase) Then Continue For
                If String.Equals(subId, "Recent", StringComparison.OrdinalIgnoreCase) Then Continue For
                If String.IsNullOrEmpty(subName) Then Continue For
                ' Remember name→id for artwork emission (Album-as-ValueDistinct level).
                podcastSubIdByAlbumName(subName) = subId
                podcastSubNameBySubId(subId) = subName
                podcastSubIdBySlug(PodcastSlug(subId)) = subId
                Dim episodeUrls() As String = Nothing
                Try
                    If Not mbApiInterface.Podcasts_GetSubscriptionEpisodes(subId, episodeUrls) Then Continue For
                Catch ex As Exception
                    LogError(ex, "LoadPodcastFiles.GetSubscriptionEpisodes", "id=" & subId)
                    Continue For
                End Try
                If episodeUrls Is Nothing Then Continue For
                For epIdx As Integer = 0 To episodeUrls.Length - 1
                    Dim episode() As String = Nothing
                    Try
                        If Not mbApiInterface.Podcasts_GetSubscriptionEpisode(subId, epIdx, episode) Then Continue For
                    Catch ex As Exception
                        LogError(ex, "LoadPodcastFiles.GetSubscriptionEpisode", "subId=" & subId & ",idx=" & epIdx)
                        Continue For
                    End Try
                    If episode Is Nothing Then Continue For
                    Dim epUrl As String = If(episode.Length > PodcastEpisodeUrlIdx AndAlso Not String.IsNullOrEmpty(episode(PodcastEpisodeUrlIdx)), _
                                              episode(PodcastEpisodeUrlIdx), _
                                              episodeUrls(epIdx))
                    Dim epTitle As String = If(episode.Length > PodcastEpisodeTitleIdx, episode(PodcastEpisodeTitleIdx), "")
                    If String.IsNullOrEmpty(epUrl) Then Continue For
                    Dim tags() As String = Nothing
                    EnsureTagSlots(tags)
                    ' Seed "0" for numeric-typed slots WriteAudioFileDIDL parses unconditionally
                    ' (Duration, Size, dates, counts, rating). Empty string would round-trip into
                    ' CLng/Long.TryParse failures.
                    tags(MetaDataIndex.Duration) = "0"
                    tags(MetaDataIndex.Size) = "0"
                    tags(MetaDataIndex.DateAdded) = "0"
                    tags(MetaDataIndex.DateLastPlayed) = "0"
                    tags(MetaDataIndex.PlayCount) = "0"
                    tags(MetaDataIndex.Rating) = "0"
                    tags(MetaDataIndex.Url) = epUrl
                    ' A downloaded episode is a real file on disk - pull its actual duration
                    ' (ticks) and size (bytes) from MB so the DIDL advertises a concrete length.
                    ' Otherwise res@duration is absent and every play triggers a fresh ffprobe
                    ' metadata extraction on the renderer. Non-downloaded (http) episodes have no
                    ' local file yet, so they keep the "0" seed and fall back to on-play probing.
                    If IO.Path.IsPathRooted(epUrl) Then
                        Try
                            Dim fileTags() As String = Nothing
                            If mbApiInterface.Library_GetFileTags(epUrl, podcastDurationSizeFields, fileTags) AndAlso fileTags IsNot Nothing Then
                                If fileTags.Length > 0 AndAlso Not String.IsNullOrEmpty(fileTags(0)) Then tags(MetaDataIndex.Duration) = fileTags(0)
                                If fileTags.Length > 1 AndAlso Not String.IsNullOrEmpty(fileTags(1)) Then tags(MetaDataIndex.Size) = fileTags(1)
                            End If
                        Catch ex As Exception
                            LogError(ex, "LoadPodcastFiles.DurationSize", "url=" & epUrl)
                        End Try
                        ' Size straight from disk if MB didn't supply it (duration is the one that
                        ' stops the re-probe; size is emitted alongside it when present).
                        If tags(MetaDataIndex.Size) = "0" Then
                            Try
                                tags(MetaDataIndex.Size) = New IO.FileInfo(epUrl).Length.ToString()
                            Catch
                            End Try
                        End If
                    End If
                    tags(MetaDataIndex.Title) = epTitle
                    tags(MetaDataIndex.Album) = subName
                    ' MB's podcast tuple carries no author, but the SHOW is the podcast's
                    ' artist - so mirror the subscription name into the artist-family slots
                    ' (Artist / AlbumArtist and their Sort variants), same as it already fills
                    ' Album. Without this every artist slot is empty, so a browse path that
                    ' groups podcasts by an artist field (e.g. SortAlbumArtist → slot 26) sees
                    ' zero distinct values and dead-ends to an empty level. Consistent with
                    ' Album = show, not invented data.
                    tags(MetaDataIndex.Artist) = subName
                    tags(MetaDataIndex.AlbumArtist) = subName
                    tags(MetaDataIndex.SortArtist) = subName
                    tags(MetaDataIndex.AlbumArtistSort) = subName
                    ' Publish year (from ep[2]) into the Year slot so a podcast path can group
                    ' episodes by year - the only year data the subscription tuple carries.
                    If episode.Length > PodcastEpisodeDateIdx Then
                        Dim yr As String = ExtractYear(episode(PodcastEpisodeDateIdx))
                        If yr.Length > 0 Then tags(MetaDataIndex.Year) = yr
                    End If
                    ' Synthetic Folder slot - picked up by the "Folder" field name when the
                    ' user wires it into a grouping path (e.g. Podcasts → group by Folder).
                    tags(MetaDataIndex.ExtraField1) = subFolder
                    Dim id As String = GetFileId(epUrl)
                    If fileLookup.ContainsKey(id) Then
                        Dim attempt As Integer = 1
                        Dim newId As String = id
                        Do While fileLookup.ContainsKey(newId) AndAlso attempt < 100
                            newId = id.Substring(0, id.Length - 2) & attempt.ToString("X2")
                            attempt += 1
                        Loop
                        id = newId
                    End If
                    fileLookup.Add(id, tags)
                    podcastFiles.Add(tags)
                    ' Remember the subscription this episode belongs to so the DIDL
                    ' emitter can fetch its artwork via Podcasts_GetSubscriptionArtwork
                    ' (see WriteLazyContainer artwork block + /PodcastThumbnail route).
                    podcastSubIdByFileId(id) = subId
                    ' Capture the on-disk subscription folder once per sub - the parent
                    ' directory of any downloaded episode is the folder where MB drops
                    ' folder.jpg / cover.jpg. Skip non-local urls (http feed urls before
                    ' the episode is downloaded) by checking IsPathRooted.
                    If Not podcastFolderBySubId.ContainsKey(subId) Then
                        Try
                            If Not String.IsNullOrEmpty(epUrl) AndAlso IO.Path.IsPathRooted(epUrl) Then
                                Dim dir As String = IO.Path.GetDirectoryName(epUrl)
                                If Not String.IsNullOrEmpty(dir) Then
                                    podcastFolderBySubId(subId) = dir
                                End If
                            End If
                        Catch
                        End Try
                    End If
                Next
            Next
            ' Post-load sanity probe: dump aggregate stats + first few entries' grouping
            ' values so we can see if ExtraField1 (MusicBee Folder) actually reached the
            ' tag arrays. Same one-shot gate as the layout probe - writes unconditionally.
            Try
                Dim probePath As String = mbApiInterface.Setting_GetPersistentStoragePath() & "PodcastLoaded.txt"
                Using w As New IO.StreamWriter(probePath, False)
                    w.WriteLine("Podcast loader summary - " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                    w.WriteLine("Total episode entries in podcastFiles: " & podcastFiles.Count)
                    w.WriteLine("")
                    w.WriteLine("First 20 entries: folder | album | title | url")
                    Dim cap As Integer = Math.Min(20, podcastFiles.Count)
                    For i As Integer = 0 To cap - 1
                        Dim tt() As String = podcastFiles(i)
                        w.WriteLine("  ""[" & tt(MetaDataIndex.ExtraField1) & "]"" | ""[" & tt(MetaDataIndex.Album) & "]"" | """ & tt(MetaDataIndex.Title) & """ | " & tt(MetaDataIndex.Url))
                    Next
                    ' Histogram by folder so we can confirm grouping will work.
                    Dim byFolder As New Dictionary(Of String, Integer)
                    For Each tt As String() In podcastFiles
                        Dim f As String = tt(MetaDataIndex.ExtraField1)
                        Dim n As Integer
                        byFolder.TryGetValue(f, n)
                        byFolder(f) = n + 1
                    Next
                    w.WriteLine("")
                    w.WriteLine("Episodes per MusicBee Folder:")
                    For Each kv As KeyValuePair(Of String, Integer) In byFolder
                        w.WriteLine("  """ & kv.Key & """  → " & kv.Value)
                    Next
                End Using
            Catch ex As Exception
                LogError(ex, "LoadPodcastFiles.LoadedProbe")
            End Try
            LogInformation("LazyQuery", "[LoadPodcastFiles] finalCount=" & podcastFiles.Count)
        End Sub

        ' yaiol - Audiobook + Inbox shared partitioner.
        '
        ' MusicBee's query parser rejects single-domain queries like "domain=AudioBooks"
        ' and "domain=Inbox" in this version. Only "domain=Music+AudioBooks+Inbox" works,
        ' returning every file across all three. To partition: check each file's Category
        ' tag - MB sets it to the localized section name ("Music" / "Audiobooks" /
        ' "Inbox"). This is the same approach the original eager LoadLibrary used (see
        ' git history v2.0.5 ItemManager.vb lines 107-121), kept here to honor a proven
        ' contract.
        '
        ' Cost: O(N) per first-access partition pass, where N = 325k for a large library.
        ' Done once per session (gated by domainsPartitioned), shared between the
        ' audiobook and inbox endpoints so the second entry is free. The music endpoint
        ' does NOT use this - it keeps its smart-playlist drill path (no need to materia-
        ' lize the whole library at root entry).
        Private Shared domainsPartitioned As Boolean = False

        Private Shared Sub PartitionMusicDomains()
            If domainsPartitioned Then Return
            audiobookFiles = New List(Of String())
            inboxFiles = New List(Of String())
            Dim urls() As String = Nothing
            Dim ok As Boolean = mbApiInterface.Library_QueryFilesEx("domain=Music+AudioBooks+Inbox", urls)
            Dim total As Integer = If(urls Is Nothing, 0, urls.Length)
            If urls IsNot Nothing Then
                For Each url As String In urls
                    ' MetaDataType.Category lives in the inner ItemManager-nested enum
                    ' (value 42), not in Plugin.MetaDataType. Cast it across the same way
                    ' LazyFieldNameToFetchTag does for YearOnly.
                    Dim cat As String = mbApiInterface.Library_GetFileTag(url, CType(MetaDataType.Category, Plugin.MetaDataType))
                    If cat Is Nothing Then Continue For
                    Dim isAudiobook As Boolean = (String.Compare(cat, audiobookCategory, StringComparison.Ordinal) = 0)
                    Dim isInbox As Boolean = (String.Compare(cat, inboxCategory, StringComparison.Ordinal) = 0)
                    If Not (isAudiobook OrElse isInbox) Then Continue For
                    Dim tags() As String
                    Try
                        SyncLock fileLookup
                            tags = LoadFile(url)
                        End SyncLock
                    Catch ex As Exception
                        LogError(ex, "PartitionMusicDomains.LoadFile", "url=" & url)
                        Continue For
                    End Try
                    If tags Is Nothing OrElse String.IsNullOrEmpty(tags(MetaDataIndex.Url)) Then Continue For
                    If isInbox Then
                        inboxFiles.Add(tags)
                    Else
                        audiobookFiles.Add(tags)
                    End If
                Next
            End If
            domainsPartitioned = True
            LogInformation("LazyQuery", "[PartitionMusicDomains] broad ok=" & ok & " total=" & total & " audiobook=" & audiobookFiles.Count & " inbox=" & inboxFiles.Count)
        End Sub

        Private Shared Sub LoadAudiobookFiles()
            PartitionMusicDomains()
        End Sub

        Private Shared Sub LoadInboxFiles()
            PartitionMusicDomains()
        End Sub

        ' Build the root TemplateNode+FolderNode pair for an endpoint (Music, Audiobooks,
        ' Inbox) from its binding's Paths. Two cases:
        '   Single path  → flat. The endpoint container itself adopts the path's hierarchy:
        '                  its Folders = path's sub-folders, Fields = path's BuildFieldsForPath.
        '                  The user lands directly inside the grouping with no extra click.
        '   Multi path   → wrap. The endpoint becomes a folder of named path-views, each path
        '                  is a child container with auto-generated label (FormatBrowsePathDisplay).
        ' baseId is the endpoint's container ID at root ("1" Music, "115" Audiobooks, ...).
        ' Multi-path child IDs are "<baseId>_<i>"; single-path inherits baseId directly.
        Private Shared Sub BuildEndpointTree(baseId As String, categoryName As String, binding As EndpointBinding, files As List(Of String()), ByRef rootTemplate As TemplateNode, ByRef rootFolder As FolderNode)
            Dim paths As BrowsePath() = If(binding IsNot Nothing AndAlso binding.Paths IsNot Nothing AndAlso binding.Paths.Length > 0, _
                                            binding.Paths, _
                                            New BrowsePath() {New BrowsePath With {.Hierarchy = New HierarchyEntry() {}, .Leaf = LeafMode.AT}})
            If paths.Length = 1 Then
                Dim p As BrowsePath = paths(0)
                Dim pathFolder As FolderNode = BuildHierarchicalFolderNode(files, p.Hierarchy, p.Leaf, p.IncludeAllTracks, p.AlbumGroupBy)
                Dim pathFields() As MetaDataIndex = BuildFieldsForPath(p)
                rootTemplate = New TemplateNode(baseId, "0", baseId, categoryName, "object.container", pathFields)
                rootFolder = New FolderNode(categoryName) With {
                    .Folders = pathFolder.Folders,
                    .ChildFiles = pathFolder.ChildFiles,
                    .IsBucket = pathFolder.IsBucket
                }
            Else
                ' Multi-path: PARTIAL merge by first hierarchy field. Group paths by
                ' the field at hierarchy[0]; each group becomes one top-level child
                ' under the endpoint. Singleton groups render as today's flat wrap.
                ' Groups of 2+ paths recursively prefix-merge to find their own deeper
                ' shared prefix and produce a collapsed sub-tree ending in a divergence.
                BuildPartialMergedMultiPathRoot(baseId, categoryName, paths, files, rootTemplate, rootFolder)
            End If
        End Sub

        ' Group multi-path entries by their first hierarchy field (empty-hierarchy paths
        ' group under ""), then render each group as one top-level child under the
        ' endpoint. Singleton groups → standalone wrap (today's flat-wrap behaviour).
        ' Multi-member groups → prefix-merged sub-tree (shared prefix collapsed, divergence
        ' at the bottom). Group order preserves the binding's path order at first
        ' appearance.
        Private Shared Sub BuildPartialMergedMultiPathRoot(baseId As String, categoryName As String, paths As BrowsePath(), files As List(Of String()), ByRef rootTemplate As TemplateNode, ByRef rootFolder As FolderNode)
            ' Derive ParentId/Id from the dotted baseId so this function works both as
            ' an endpoint root (baseId = "1", "115", …) AND as a recursive divergence
            ' wrapper (baseId = "1_3_0", …). Top-level endpoints have no underscore in
            ' baseId → ParentId="0" Id=baseId, matching original behaviour.
            Dim sep As Integer = baseId.LastIndexOf("_"c)
            Dim parentId As String = If(sep > 0, baseId.Substring(0, sep), "0")
            Dim selfId As String = If(sep > 0, baseId.Substring(sep + 1), baseId)
            rootTemplate = New TemplateNode(baseId, parentId, selfId, categoryName, "object.container", Nothing)
            rootFolder = New FolderNode(categoryName)
            ' Group + preserve first-appearance order.
            Dim groupKeys As New List(Of String)
            Dim groups As New Dictionary(Of String, List(Of BrowsePath))(StringComparer.OrdinalIgnoreCase)
            For Each p As BrowsePath In paths
                Dim k As String = ""
                If p.Hierarchy IsNot Nothing AndAlso p.Hierarchy.Length > 0 AndAlso p.Hierarchy(0) IsNot Nothing Then
                    k = If(p.Hierarchy(0).Field, "")
                End If
                If Not groups.ContainsKey(k) Then
                    groups.Add(k, New List(Of BrowsePath))
                    groupKeys.Add(k)
                End If
                groups(k).Add(p)
            Next
            Dim childTemplates As New List(Of TemplateNode)
            Dim childFolders As New List(Of FolderNode)
            Dim idx As Integer = 0
            For Each k As String In groupKeys
                Dim groupPaths As BrowsePath() = groups(k).ToArray()
                Dim childTemplate As TemplateNode = Nothing
                Dim childFolder As FolderNode = Nothing
                If groupPaths.Length = 1 Then
                    BuildSinglePathChild(groupPaths(0), files, baseId, idx, childTemplate, childFolder)
                Else
                    BuildMergedGroupChild(groupPaths, files, baseId, idx, childTemplate, childFolder)
                End If
                childTemplates.Add(childTemplate)
                childFolders.Add(childFolder)
                idx += 1
            Next
            rootTemplate.ChildNodes = childTemplates.ToArray()
            rootFolder.Folders = childFolders.ToArray()
        End Sub

        ' Render one path as a top-level child of the endpoint. Same as the old flat-
        ' wrap behaviour for a single path: BuildHierarchicalFolderNode produces the
        ' full hierarchy starting from the path's first field.
        Private Shared Sub BuildSinglePathChild(p As BrowsePath, files As List(Of String()), baseId As String, idx As Integer, ByRef childTemplate As TemplateNode, ByRef childFolder As FolderNode)
            Dim label As String = View.FormatBrowsePathShortDisplay(p)
            childFolder = BuildHierarchicalFolderNode(files, p.Hierarchy, p.Leaf, p.IncludeAllTracks, p.AlbumGroupBy)
            childFolder.Name = label
            Dim pathFields() As MetaDataIndex = BuildFieldsForPath(p)
            Dim pathId As String = idx.ToString()
            childTemplate = New TemplateNode(baseId & "_" & pathId, baseId, pathId, label, "object.container", pathFields)
        End Sub

        ' Render a group of 2+ paths that all share the same first hierarchy field.
        ' Builds BOTH a folder tree AND a matching template tree (necessary for
        ' Browse to navigate via TryLocateNode → WriteContainerItemsDIDL). The
        ' folder tree mirrors the merged structure; the template tree mirrors
        ' the folder tree exactly so each level has the navigation metadata it
        ' needs.
        Private Shared Sub BuildMergedGroupChild(groupPaths As BrowsePath(), files As List(Of String()), baseId As String, idx As Integer, ByRef childTemplate As TemplateNode, ByRef childFolder As FolderNode)
            Dim sharedPrefix As List(Of HierarchyEntry) = ComputeSharedPrefix(groupPaths)
            If sharedPrefix.Count = 0 Then
                ' Defensive fallback (shouldn't fire because group members all share
                ' first field - but just in case). Wrap as single-path-each container.
                Dim wrapperLabel As String = View.FieldDisplayName(If(groupPaths(0).Hierarchy(0).Field, ""))
                Dim wrapperT As TemplateNode = Nothing
                Dim wrapperF As FolderNode = Nothing
                BuildWrappedMultiPathRoot(baseId & "_" & idx, wrapperLabel, groupPaths, files, wrapperT, wrapperF)
                childTemplate = wrapperT
                childFolder = wrapperF
                Return
            End If
            ' Strip the shared prefix from each path; the remainder is what diverges.
            Dim remainingPaths(groupPaths.Length - 1) As BrowsePath
            For i As Integer = 0 To groupPaths.Length - 1
                Dim p As BrowsePath = groupPaths(i)
                Dim rest(p.Hierarchy.Length - sharedPrefix.Count - 1) As HierarchyEntry
                For j As Integer = sharedPrefix.Count To p.Hierarchy.Length - 1
                    rest(j - sharedPrefix.Count) = p.Hierarchy(j)
                Next
                remainingPaths(i) = New BrowsePath With {
                    .Hierarchy = rest,
                    .Leaf = p.Leaf,
                    .IncludeAllTracks = p.IncludeAllTracks,
                    .AlbumGroupBy = p.AlbumGroupBy
                }
            Next
            ' Build the chain joint (folder + template) from level 0 downward.
            Dim chainId As String = baseId & "_" & idx.ToString()
            BuildChainJoint(sharedPrefix, 0, remainingPaths, files, chainId, childTemplate, childFolder)
            ' Override the chain root's label to the shared field's display name
            ' (e.g. "Genre"). Inner chain levels get their value as their name.
            childTemplate.Name = View.FieldDisplayName(sharedPrefix(0).Field)
            childFolder.Name = childTemplate.Name
        End Sub

        ' Joint folder+template builder for the merged sub-tree. Recursively walks
        ' sharedPrefix levels grouping files by each field, then renders the
        ' divergence at the bottom. Produces parallel structures so Browse can
        ' navigate via ChildNodes (Fields=Nothing on chain levels, Fields set on
        ' divergence-branch leaves).
        '
        ' At chain levels (level < sharedPrefix.Count):
        '   template.Fields = Nothing, template.ChildNodes = sub-templates per value
        '   folder.Folders = sub-folders per value
        '
        ' At divergence wrapper (level == sharedPrefix.Count):
        '   template.Fields = Nothing, template.ChildNodes = one per remaining path
        '   folder.Folders = one per remaining path
        '
        ' Each divergence branch (its own template, Fields = BuildFieldsForPath of
        ' the stripped path, ChildNodes = Nothing) gets the BuildHierarchicalFolderNode
        ' tree as its folder - Browse drills positionally into that subtree.
        Private Shared Sub BuildChainJoint(sharedPrefix As List(Of HierarchyEntry), level As Integer, remainingPaths As BrowsePath(), files As List(Of String()), currentPath As String, ByRef tmpl As TemplateNode, ByRef fld As FolderNode)
            ' Compute Path / ParentId / Id from the dotted currentPath.
            Dim sep As Integer = currentPath.LastIndexOf("_"c)
            Dim parentId As String = If(sep > 0, currentPath.Substring(0, sep), "0")
            Dim selfId As String = If(sep > 0, currentPath.Substring(sep + 1), currentPath)
            If level >= sharedPrefix.Count Then
                ' Divergence point - recursively partial-merge the remaining paths.
                ' BuildPartialMergedMultiPathRoot groups by first field, merges groups
                ' of 2+, renders singletons standalone. Calling it here propagates the
                ' prefix-merge behaviour to any nested shared prefixes inside
                ' remainingPaths (e.g. 3 paths starting with [Year, …] [Year, …]
                ' [Decade, …] - the first two sub-merge under Year, Decade stands alone).
                ' Termination: each recursion strips at least one hierarchy field;
                ' bounded by max hierarchy depth.
                BuildPartialMergedMultiPathRoot(currentPath, "", remainingPaths, files, tmpl, fld)
                Return
            End If
            ' Chain level - group files by the shared field's value, recurse per value.
            Dim entry As HierarchyEntry = sharedPrefix(level)
            Dim fieldMDI As MetaDataIndex = FieldNameToMetaDataIndex(entry.Field)
            If fieldMDI = MetaDataIndex.None Then
                ' Unknown field - skip and continue. Pass through one level deeper.
                BuildChainJoint(sharedPrefix, level + 1, remainingPaths, files, currentPath, tmpl, fld)
                Return
            End If
            Dim grouped As Dictionary(Of String, List(Of String())) = GroupFilesByHierarchyEntry(files, fieldMDI, entry.BucketByLetter)
            Dim sortedKeys As List(Of String) = SortHierarchyKeys(grouped, entry.SortDescending)
            Dim subFolders2(sortedKeys.Count - 1) As FolderNode
            Dim subTemplates2(sortedKeys.Count - 1) As TemplateNode
            For i As Integer = 0 To sortedKeys.Count - 1
                Dim k As String = sortedKeys(i)
                Dim childPath As String = currentPath & "_" & i.ToString()
                Dim sT As TemplateNode = Nothing
                Dim sF As FolderNode = Nothing
                BuildChainJoint(sharedPrefix, level + 1, remainingPaths, grouped(k), childPath, sT, sF)
                sT.Name = k
                sF.Name = k
                subTemplates2(i) = sT
                subFolders2(i) = sF
            Next
            tmpl = New TemplateNode(currentPath, parentId, selfId, "", "object.container", Nothing)
            tmpl.ChildNodes = subTemplates2
            fld = New FolderNode("", subFolders2, files)
        End Sub

        ' Today's wrap behaviour: one labelled sub-container per path under the endpoint.
        ' Used when paths don't share any hierarchy prefix.
        Private Shared Sub BuildWrappedMultiPathRoot(baseId As String, categoryName As String, paths As BrowsePath(), files As List(Of String()), ByRef rootTemplate As TemplateNode, ByRef rootFolder As FolderNode)
            rootTemplate = New TemplateNode(baseId, "0", baseId, categoryName, "object.container", Nothing)
            rootFolder = New FolderNode(categoryName)
            Dim childTemplates As New List(Of TemplateNode)
            Dim childFolders As New List(Of FolderNode)
            For i As Integer = 0 To paths.Length - 1
                Dim p As BrowsePath = paths(i)
                Dim pathLabel As String = View.FormatBrowsePathShortDisplay(p)
                Dim pathFolder As FolderNode = BuildHierarchicalFolderNode(files, p.Hierarchy, p.Leaf, p.IncludeAllTracks, p.AlbumGroupBy)
                pathFolder.Name = pathLabel
                Dim pathFields() As MetaDataIndex = BuildFieldsForPath(p)
                Dim pathId As String = i.ToString()
                childTemplates.Add(New TemplateNode(baseId & "_" & pathId, baseId, pathId, pathLabel, "object.container", pathFields))
                childFolders.Add(pathFolder)
            Next
            rootTemplate.ChildNodes = childTemplates.ToArray()
            rootFolder.Folders = childFolders.ToArray()
        End Sub

        ' Compute the longest hierarchy prefix shared by ALL paths. Two HierarchyEntry
        ' values at the same position are considered equal when their Field name,
        ' BucketByLetter, and SortDescending all match. Any path with empty hierarchy
        ' or a divergent entry breaks the prefix.
        Private Shared Function ComputeSharedPrefix(paths As BrowsePath()) As List(Of HierarchyEntry)
            Dim result As New List(Of HierarchyEntry)
            If paths Is Nothing OrElse paths.Length < 2 Then Return result
            ' Any empty-hierarchy path → no merge possible.
            For Each p As BrowsePath In paths
                If p.Hierarchy Is Nothing OrElse p.Hierarchy.Length = 0 Then Return result
            Next
            Dim minLen As Integer = paths(0).Hierarchy.Length
            For Each p As BrowsePath In paths
                If p.Hierarchy.Length < minLen Then minLen = p.Hierarchy.Length
            Next
            For i As Integer = 0 To minLen - 1
                Dim e0 As HierarchyEntry = paths(0).Hierarchy(i)
                Dim allMatch As Boolean = True
                For Each p As BrowsePath In paths
                    Dim e As HierarchyEntry = p.Hierarchy(i)
                    If e Is Nothing OrElse e0 Is Nothing OrElse _
                       Not String.Equals(e.Field, e0.Field, StringComparison.OrdinalIgnoreCase) OrElse _
                       e.BucketByLetter <> e0.BucketByLetter OrElse _
                       e.SortDescending <> e0.SortDescending Then
                        allMatch = False
                        Exit For
                    End If
                Next
                If Not allMatch Then Exit For
                result.Add(e0)
                ' Stop at the FIRST level where any path would terminate at the next step,
                ' because we still need at least one remaining hierarchy entry OR a leaf
                ' rendering per path for the divergence to be meaningful. Continue past
                ' that point and the "divergence" is just identical leaf renderings.
            Next
            ' If the shared prefix consumes ALL of every path, there's no divergence to
            ' show - the paths are functionally identical. Treat as no merge: caller
            ' falls back to wrap and the user sees duplicate labels (a UI signal that
            ' their template has redundant paths).
            For Each p As BrowsePath In paths
                If p.Hierarchy.Length <= result.Count Then
                    result.Clear()
                    Exit For
                End If
            Next
            Return result
        End Function

        Private Shared Function LoadFile(url As String) As String()
            Dim tags() As String
            mbApiInterface.Library_GetFileTags(url, queryFields, tags)
            EnsureTagSlots(tags)
            SanitizeTags(tags)
            Dim id As String = GetFileId(url)
            If Not fileLookup.ContainsKey(id) Then
                fileLookup.Add(id, tags)
            End If
            Return tags
        End Function

        ' Pad a tags() array up to TagArraySize so synthetic slots (ExtraField1, …) are
        ' always indexable. ReDim Preserve keeps existing values; new slots default to
        ' empty string (their natural "absent" value).
        Friend Shared Sub EnsureTagSlots(ByRef tags() As String)
            If tags Is Nothing Then
                ReDim tags(TagArraySize - 1)
                For i As Integer = 0 To tags.Length - 1
                    tags(i) = ""
                Next
                Return
            End If
            If tags.Length < TagArraySize Then
                Dim oldLen As Integer = tags.Length
                ReDim Preserve tags(TagArraySize - 1)
                For i As Integer = oldLen To tags.Length - 1
                    tags(i) = ""
                Next
            End If
        End Sub

        ' XML 1.0 forbids most C0 control characters (0x00-0x08, 0x0B, 0x0C, 0x0E-0x1F) plus a few high-range
        ' codepoints. MusicBee's library can contain such characters in tags (rare, but happens - bad imports,
        ' copy-pastes from corrupt sources, e.g. the UTF-8 -> Latin-1 -> back encoding bug that mangles
        ' U+2019 's trailing 0x99 into 0x19). Strip them at the entry point so every downstream XmlWriter
        ' call is safe. The companion fix_0x19.py tool repairs source files in-place; this sanitizer remains
        ' as a safety net for any future stray control chars that slip through.
        Private Shared Sub SanitizeTags(tags() As String)
            If tags Is Nothing Then Return
            For i As Integer = 0 To tags.Length - 1
                Dim original As String = tags(i)
                If String.IsNullOrEmpty(original) Then Continue For
                Dim cleaned As String = SanitizeXmlString(original)
                If Not Object.ReferenceEquals(cleaned, original) Then
                    tags(i) = cleaned
                End If
            Next
        End Sub

        Private Shared Function SanitizeXmlString(s As String) As String
            If String.IsNullOrEmpty(s) Then Return s
            Dim hasInvalid As Boolean = False
            For Each c As Char In s
                If Not XmlConvert.IsXmlChar(c) Then
                    hasInvalid = True
                    Exit For
                End If
            Next
            If Not hasInvalid Then Return s
            Dim sb As New StringBuilder(s.Length)
            For Each c As Char In s
                If XmlConvert.IsXmlChar(c) Then
                    sb.Append(c)
                End If
            Next
            Return sb.ToString()
        End Function

        Private Shared Function LoadLibraryPlaylists(wmcCompatability As Boolean) As FolderNode
            mbApiInterface.Playlist_QueryPlaylists()
            Dim playlists As New PlaylistFolderNode(playlistsCategory)
            Dim folder As PlaylistFolderNode
            Dim playlist As PlaylistFolderNode
            ' Now Playing is no longer nested inside the Playlists container - it's now a
            ' top-level endpoint at root (see the root composer in LoadLibrary). The legacy
            ' "NowPlaying" path-resolution code in WriteContainerItemsDIDL still applies for
            ' the new root-level node.
            Do
                Dim playlistUrl As String = mbApiInterface.Playlist_QueryGetNextPlaylist()
                If playlistUrl Is Nothing Then
                    Exit Do
                End If
                If mbApiInterface.Playlist_GetType(playlistUrl) <> PlaylistFormat.Radio Then
                    Dim playlistFullName As String = mbApiInterface.Playlist_GetName(playlistUrl)
                    ' Browse-views: skip playlist if its binding says not exposed. The Hidden
                    ' template (Views tab Apply) toggles binding.Exposed; missing bindings (e.g.
                    ' the auto-stamped default on first discovery) treat the playlist as exposed.
                    Dim plBinding As EndpointBinding = View.GetBinding("playlist:" & playlistFullName)
                    If plBinding IsNot Nothing AndAlso Not plBinding.Exposed Then
                        Continue Do
                    End If
                    folder = playlists
                    Dim values() As String
                    If wmcCompatability Then
                        values = New String() {playlistFullName}
                    Else
                        values = playlistFullName.Split("\"c)
                        For index As Integer = 0 To values.Length - 2
                            Dim name As String = values(index)
                            Dim matched As Boolean = False
                            For index2 As Integer = 0 To folder.Folders.Count - 1
                                If String.Compare(folder.Folders(index2).Name, name, StringComparison.OrdinalIgnoreCase) = 0 Then
                                    matched = True
                                    folder = folder.Folders(index2)
                                    Exit For
                                End If
                            Next index2
                            If Not matched Then
                                ' Bug fix: must descend into the newly-created folder so subsequent path
                                ' components attach inside it. Without this, the next path segment (and
                                ' eventually the playlist itself) lands on the parent's level, which is
                                ' what makes the first playlist in each folder appear orphaned at root.
                                Dim newFolder As New PlaylistFolderNode(name)
                                folder.Folders.Add(newFolder)
                                folder = newFolder
                            End If
                        Next index
                    End If
                    playlist = New PlaylistFolderNode(values(values.Length - 1)) With {
                        .Path = playlistUrl
                    }
                    folder.Folders.Add(playlist)
                End If
            Loop
            Dim playlistRoot As New FolderNode
            LoadPlaylistFolder(playlists, playlistRoot)
            ' Browse-views: for each leaf playlist with a non-trivial binding, eager-build the
            ' hierarchical sub-tree (artist→album→tracks etc.) so Browse can drill through it.
            WirePlaylistBindings(playlistRoot)
            Return playlistRoot
        End Function

        Private Shared Sub LoadLibraryFilters()
            filterFolderNodes = New List(Of FolderNode)
            filterTemplateNodes = New List(Of TemplateNode)
            Dim filtersDir As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicBee", "Filters")
            If Not Directory.Exists(filtersDir) Then
                Return
            End If
            Dim filterFiles() As String = Directory.GetFiles(filtersDir, "*.xautopf")
            Array.Sort(filterFiles, StringComparer.CurrentCultureIgnoreCase)
            ' Browse-views: discover + auto-stamp bindings for everything we'll load this pass.
            ' Safe to call repeatedly - only new endpoints get default-stamped.
            Try
                View.EnsureEndpointBindings(View.DiscoverEndpoints())
            Catch
                ' Best-effort; binding ensure failure shouldn't break filter loading.
            End Try
            For index As Integer = 0 To filterFiles.Length - 1
                Dim filterPath As String = filterFiles(index)
                Dim filterName As String = Path.GetFileNameWithoutExtension(filterPath)
                ' Browse-views: skip filter if its binding says not exposed. (HiddenFilters was
                ' retired - hide is now driven exclusively by binding.Exposed via the Views
                ' tab's Visibility checkbox.)
                Dim binding As EndpointBinding = View.GetBinding("filter:" & filterName)
                If binding IsNot Nothing AndAlso Not binding.Exposed Then
                    Continue For
                End If
                Dim xml As String
                Try
                    xml = File.ReadAllText(filterPath)
                Catch
                    Continue For
                End Try
                Dim filenames() As String = Nothing
                Try
                    mbApiInterface.Library_QueryFilesEx(xml, filenames)
                Catch
                    Continue For
                End Try
                If filenames Is Nothing OrElse filenames.Length = 0 Then
                    Continue For
                End If
                Dim files As New List(Of String())
                For fileIndex As Integer = 0 To filenames.Length - 1
                    files.Add(LookupOrLoadFile(filenames(fileIndex)))
                Next fileIndex
                Dim filterId As String = (200 + index).ToString()
                ' Multi-path rendering. Each path in the binding is its own view of the filter's
                ' tracks.
                '   • Single path (or zero): keep today's flat behavior - the filter directly
                '     renders that path's hierarchy with no extra wrapper level.
                '   • Multiple paths: wrap. The filter becomes a folder containing one sub-
                '     container per path. Each sub-container is the result of grouping the
                '     filter's tracks by that path. Sub-container label auto-generates from
                '     Hierarchy + Leaf via View.FormatBrowsePathDisplay.
                Dim paths As BrowsePath() = If(binding IsNot Nothing AndAlso binding.Paths IsNot Nothing AndAlso binding.Paths.Length > 0, _
                                                binding.Paths, _
                                                New BrowsePath() {New BrowsePath With {.Hierarchy = New HierarchyEntry() {}, .Leaf = LeafMode.AT}})
                If paths.Length = 1 Then
                    Dim p As BrowsePath = paths(0)
                    Dim folderNode As FolderNode = BuildHierarchicalFolderNode(files, p.Hierarchy, p.Leaf, p.IncludeAllTracks, p.AlbumGroupBy)
                    folderNode.Name = filterName
                    Dim fields() As MetaDataIndex = BuildFieldsForPath(p)
                    Dim templateNode As New TemplateNode(filterId, "0", filterId, filterName, "object.container", fields) With {
                        .Category = ContainerCategory.Playlist
                    }
                    filterFolderNodes.Add(folderNode)
                    filterTemplateNodes.Add(templateNode)
                Else
                    Dim subTemplates(paths.Length - 1) As TemplateNode
                    Dim subFolders(paths.Length - 1) As FolderNode
                    For i As Integer = 0 To paths.Length - 1
                        Dim p As BrowsePath = paths(i)
                        Dim pathLabel As String = View.FormatBrowsePathShortDisplay(p)
                        Dim pathFolder As FolderNode = BuildHierarchicalFolderNode(files, p.Hierarchy, p.Leaf, p.IncludeAllTracks, p.AlbumGroupBy)
                        pathFolder.Name = pathLabel
                        Dim pathFields() As MetaDataIndex = BuildFieldsForPath(p)
                        Dim pathId As String = i.ToString()
                        Dim pathTemplate As New TemplateNode(filterId & "_" & pathId, filterId, pathId, pathLabel, "object.container", pathFields) With {
                            .Category = ContainerCategory.Playlist
                        }
                        subTemplates(i) = pathTemplate
                        subFolders(i) = pathFolder
                    Next
                    ' Wrapper has no Fields (it's a folder of named path-views). TryLocateNode
                    ' descends via ChildNodes Id match; positional Browse then takes over inside
                    ' each path's sub-template.
                    Dim wrapperT As New TemplateNode(filterId, "0", filterId, filterName, "object.container", Nothing) With {
                        .Category = ContainerCategory.Playlist,
                        .ChildNodes = subTemplates
                    }
                    Dim wrapperF As New FolderNode(filterName, subFolders, files)
                    filterFolderNodes.Add(wrapperF)
                    filterTemplateNodes.Add(wrapperT)
                End If
            Next index
        End Sub

        ' Reverse lookup Plugin.MetaDataType → MetaDataIndex (= position in queryFields).
        ' Built once from the queryFields array so adding/reordering fields there is
        ' automatically reflected here.
        Private Shared ReadOnly metaDataTypeToIndex As Dictionary(Of Plugin.MetaDataType, MetaDataIndex) = BuildMetaDataTypeToIndex()

        Private Shared Function BuildMetaDataTypeToIndex() As Dictionary(Of Plugin.MetaDataType, MetaDataIndex)
            Dim d As New Dictionary(Of Plugin.MetaDataType, MetaDataIndex)
            For i As Integer = 0 To queryFields.Length - 1
                Dim mdt As Plugin.MetaDataType = queryFields(i)
                ' Skip negative values (FilePropertyType-style) and the 0 placeholder.
                If CInt(mdt) > 0 AndAlso Not d.ContainsKey(mdt) Then
                    d(mdt) = CType(i, MetaDataIndex)
                End If
            Next
            Return d
        End Function

        ' Convert a string field name from a BrowsePath.Hierarchy into our internal MetaDataIndex.
        ' Two-step: resolve name → Plugin.MetaDataType via Enum.TryParse (handles any enum name),
        ' then MetaDataType → MetaDataIndex via the queryFields reverse-lookup. Returns
        ' MetaDataIndex.None for unknown names OR for fields not pre-loaded in queryFields -
        ' caller silently drops that hierarchy level so a corrupt/extended config can't crash.
        Private Shared Function FieldNameToMetaDataIndex(name As String) As MetaDataIndex
            If String.IsNullOrEmpty(name) Then Return MetaDataIndex.None
            ' Synthetic fields - direct name → slot mapping. Bypasses the MB enum lookup
            ' (these names are not in Plugin.MetaDataType by design).
            If String.Equals(name, "MusicBeeFolder", StringComparison.OrdinalIgnoreCase) Then Return MetaDataIndex.ExtraField1
            Dim mdt As Plugin.MetaDataType = View.FieldNameToMetaDataType(name)
            If CInt(mdt) = 0 Then Return MetaDataIndex.None
            Dim idx As MetaDataIndex
            If metaDataTypeToIndex.TryGetValue(mdt, idx) Then Return idx
            Return MetaDataIndex.None
        End Function

        ' Build the Fields array a TemplateNode needs from an EndpointBinding's first path.
        ' AT leaf appends MetaDataIndex.Album so the leaf containers get the album-musicAlbum
        ' DIDL class (artwork). T leaf doesn't append - the hierarchy goes straight to tracks.
        ' Empty result falls back to legacy default {Album}.
        ' Build the Fields array for a single BrowsePath. SetContainerClass uses these to pick the
        ' DIDL container class per drill level. Each BucketByLetter entry contributes TWO slots
        ' (Letter, then the field) so the Fields length matches the rendered tree depth. The
        ' Letter slot uses MetaDataIndex.None - SetContainerClass falls back to template default.
        Private Shared Function BuildFieldsForPath(p As BrowsePath) As MetaDataIndex()
            If p Is Nothing Then Return New MetaDataIndex() {MetaDataIndex.Album}
            Dim fields As New List(Of MetaDataIndex)
            If p.Hierarchy IsNot Nothing Then
                For Each entry As HierarchyEntry In p.Hierarchy
                    If entry Is Nothing Then Continue For
                    If entry.BucketByLetter Then fields.Add(MetaDataIndex.None)
                    Dim mdi As MetaDataIndex = FieldNameToMetaDataIndex(entry.Field)
                    If mdi <> MetaDataIndex.None Then fields.Add(mdi)
                Next
            End If
            If p.Leaf = LeafMode.AT Then
                fields.Add(MetaDataIndex.Album)
            End If
            If fields.Count = 0 Then
                Return New MetaDataIndex() {MetaDataIndex.Album}
            End If
            Return fields.ToArray()
        End Function

        Private Shared Function LookupOrLoadFile(url As String) As String()
            Dim id As String = GetFileId(url)
            Dim tags() As String = Nothing
            If fileLookup.TryGetValue(id, tags) Then
                Return tags
            End If
            Return LoadFile(url)
        End Function

        ' (HierarchyToMetaDataIndices removed - BuildHierarchicalFolderNode resolves field names
        '  directly per-step, BuildFieldsForPath emits Fields with Letter-slot None entries.)

        ' Group a flat file list by Album. Each album folder is a leaf with .ChildFiles =
        ' album tracks (sorted disc/track). Used as the bottom layer when Leaf = AT.
        '
        ' albumGroupBy is the ordered (field, direction) list that defines the album: its
        ' IDENTITY (tracks sharing every field value collapse into one album), its TITLE (the
        ' non-empty field values joined by " - "), and its SORT order (album containers ordered
        ' by the field tuple, each field using its own direction). Empty/null → {Album asc}.
        ' Field values are taken WHOLE (not split on "; ") so identity matches MB's exact-tag
        ' semantics. A track is skipped only when every group field is empty.
        Private Shared Function BuildAlbumGroupingFolder(files As List(Of String()), albumGroupBy As Plugin.AlbumGroupField()) As FolderNode
            Dim fields() As Plugin.AlbumGroupField = NormalizeAlbumGroupBy(albumGroupBy)
            Dim grouped As New Dictionary(Of String, List(Of String()))(StringComparer.OrdinalIgnoreCase)
            Dim titleOf As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Dim sortValsOf As New Dictionary(Of String, String())(StringComparer.OrdinalIgnoreCase)
            For Each tags As String() In files
                Dim raws(fields.Length - 1) As String
                Dim disp(fields.Length - 1) As String
                Dim allEmpty As Boolean = True
                For i As Integer = 0 To fields.Length - 1
                    raws(i) = AlbumKeyRawValue(tags, fields(i).Field)
                    disp(i) = AlbumKeyDisplayValue(fields(i).Field, raws(i))
                    If raws(i).Length > 0 Then allEmpty = False
                Next
                If allEmpty Then Continue For
                Dim key As String = String.Join(ChrW(31), raws)   ' unit separator: never in tags
                Dim lst As List(Of String()) = Nothing
                If Not grouped.TryGetValue(key, lst) Then
                    lst = New List(Of String())
                    grouped(key) = lst
                    titleOf(key) = AlbumKeyTitle(fields, disp)
                    sortValsOf(key) = disp
                End If
                lst.Add(tags)
            Next
            Dim keys As New List(Of String)(grouped.Keys)
            keys.Sort(Function(ka As String, kb As String) As Integer
                          Dim va() As String = sortValsOf(ka)
                          Dim vb() As String = sortValsOf(kb)
                          For i As Integer = 0 To fields.Length - 1
                              Dim c As Integer = CompareAlbumKeyValue(va(i), vb(i))
                              If fields(i).SortDescending Then c = -c
                              If c <> 0 Then Return c
                          Next
                          Return 0
                      End Function)
            If keys.Count = 0 Then Return New FolderNode("", New FolderNode() {}, files)
            Dim subFolders(keys.Count - 1) As FolderNode
            For i As Integer = 0 To keys.Count - 1
                Dim albumFiles As List(Of String()) = grouped(keys(i))
                albumFiles.Sort(New AlbumFileComparer)
                subFolders(i) = New FolderNode(titleOf(keys(i)), New FolderNode() {}, albumFiles)
            Next
            Return New FolderNode("", subFolders, files)
        End Function

        ' ── Album group-by helpers (shared by the eager + lazy engines) ───────────────────
        ' Normalize an album group-by spec to a non-empty ordered field list. Strips entries
        ' with no field; empty/null result → {Album asc} so the album leaf is never keyless.
        Private Shared Function NormalizeAlbumGroupBy(spec As Plugin.AlbumGroupField()) As Plugin.AlbumGroupField()
            If spec IsNot Nothing Then
                Dim cleaned As New List(Of Plugin.AlbumGroupField)
                For Each f As Plugin.AlbumGroupField In spec
                    If f IsNot Nothing AndAlso Not String.IsNullOrEmpty(f.Field) Then cleaned.Add(f)
                Next
                If cleaned.Count > 0 Then Return cleaned.ToArray()
            End If
            Return New Plugin.AlbumGroupField() {New Plugin.AlbumGroupField("Album", False)}
        End Function

        ' Raw whole value of an album-key field for a track (not split on "; "). Unknown field
        ' or not-pre-loaded field → "". The display layer substitutes Unknown* for empties.
        ' Generic name → slot lookup: the token IS the field, no per-field aliasing (group-by
        ' is driven entirely by the path definition's field tokens).
        Private Shared Function AlbumKeyRawValue(tags As String(), field As String) As String
            Dim mdi As MetaDataIndex = FieldNameToMetaDataIndex(field)
            If mdi = MetaDataIndex.None Then Return ""
            Return If(tags(mdi), "")
        End Function

        ' Display value for an album-key field - Unknown* fallback for the album-identity
        ' fields (Album / AlbumArtist), empty otherwise.
        Private Shared Function AlbumKeyDisplayValue(field As String, raw As String) As String
            If raw.Length > 0 Then Return raw
            If String.Equals(field, "Album", StringComparison.OrdinalIgnoreCase) Then Return Plugin.L("UnknownAlbum")
            If String.Equals(field, "AlbumArtist", StringComparison.OrdinalIgnoreCase) Then Return Plugin.L("UnknownArtist")
            Return ""
        End Function

        ' Compose the album display title from the per-field display values (skip empties,
        ' join " - "). All-empty → UnknownAlbum.
        Private Shared Function AlbumKeyTitle(fields As Plugin.AlbumGroupField(), displayValues As String()) As String
            Dim parts As New List(Of String)
            For i As Integer = 0 To fields.Length - 1
                If displayValues(i).Length > 0 Then parts.Add(displayValues(i))
            Next
            If parts.Count = 0 Then Return Plugin.L("UnknownAlbum")
            Return String.Join(" - ", parts)
        End Function

        ' Per-field album-key compare: numeric when both values parse as integers (Year, etc.),
        ' else culture-aware case-insensitive string compare. Direction applied by the caller.
        Private Shared Function CompareAlbumKeyValue(a As String, b As String) As Integer
            Dim ia As Integer, ib As Integer
            If Integer.TryParse(a, ia) AndAlso Integer.TryParse(b, ib) Then Return ia.CompareTo(ib)
            Return StringComparer.CurrentCultureIgnoreCase.Compare(a, b)
        End Function

        ' Build a hierarchical FolderNode tree from a flat file list.
        ' Recursive - groups by the first hierarchy entry's field, then recurses on each group
        ' with the remaining entries. When an entry's BucketByLetter flag is set, a Letter level
        ' is inserted ABOVE the field's grouping (so [AlbumArtist, true] yields Letter→Artist
        ' rather than just Artist). When IncludeAllTracks is true, each container level adds an
        ' [All Tracks] entry as the first sub-folder that flattens everything below.
        '
        '   hierarchy=[],                       leaf=T  → flat tracks at root
        '   hierarchy=[],                       leaf=AT → album folders, each with tracks
        '   hierarchy=[(AA,F)],                 leaf=AT → artist → album → tracks
        '   hierarchy=[(AA,T)],                 leaf=AT → Letter → artist → album → tracks
        '   hierarchy=[(Genre,F),(AA,T)],       leaf=AT → genre → Letter → artist → album → tracks
        '
        ' Multi-value fields (e.g. tags(AlbumArtist) = "ArtistA; ArtistB") split into separate
        ' groups so a track with two album-artists shows under both.
        ' Recursive folder-tree builder for a binding path. Walks the hierarchy left-to-
        ' right; at each level, group files by the level's field, then recurse into each
        ' group with the remaining hierarchy. Base case (empty hierarchy) renders the leaf
        ' as either Album→Tracks (AT) or flat tracks (T).
        '
        ' Optional behaviours per level:
        '   BucketByLetter   - insert a "first letter" bucket layer before grouping by
        '                      the field value (so "by AlbumArtist" becomes A→artists,
        '                      B→artists, etc.).
        '   SortDescending   - reverse the alphabetical order of this level's groups.
        ' Path-wide behaviours:
        '   IncludeAllTracks - prepend a "[All Tracks]" folder at each level.
        '   albumGroupBy     - ordered (field, dir) list defining the AT-leaf album identity +
        '                      title + sort. Only consulted at the AT leaf. Empty → {Album}.
        Private Shared Function BuildHierarchicalFolderNode(
                files As List(Of String()),
                hierarchy() As HierarchyEntry,
                leaf As LeafMode,
                includeAllTracks As Boolean,
                albumGroupBy As Plugin.AlbumGroupField()) As FolderNode
            ' Base case: no more grouping levels. Render the leaf.
            If hierarchy Is Nothing OrElse hierarchy.Length = 0 Then
                If leaf = LeafMode.AT Then
                    Return BuildAlbumGroupingFolder(files, albumGroupBy)
                Else
                    Return New FolderNode("", New FolderNode() {}, files)
                End If
            End If
            Dim entry As HierarchyEntry = hierarchy(0)
            ' If the field name is unknown (Custom/Virtual not in queryFields, typo,
            ' renamed by user), skip this level entirely and recurse into the rest.
            Dim fieldMDI As MetaDataIndex = FieldNameToMetaDataIndex(If(entry Is Nothing, "", entry.Field))
            If fieldMDI = MetaDataIndex.None Then
                Return BuildHierarchicalFolderNode(files, hierarchy.Skip(1).ToArray(), leaf, includeAllTracks, albumGroupBy)
            End If
            ' Tree-by-slash: split values containing "/" into nested folders before
            ' continuing into the rest of the hierarchy at each leaf segment.
            If entry.TreeBySlash Then
                Dim rest() As HierarchyEntry = hierarchy.Skip(1).ToArray()
                Return BuildTreeBySlashFolderNode(files, fieldMDI, rest, leaf, includeAllTracks, albumGroupBy, entry.SortDescending)
            End If
            ' Group + sort this level.
            Dim grouped As Dictionary(Of String, List(Of String())) = GroupFilesByHierarchyEntry(files, fieldMDI, entry.BucketByLetter)
            Dim sortedKeys As List(Of String) = SortHierarchyKeys(grouped, entry.SortDescending)
            Dim childHierarchy() As HierarchyEntry = NextLevelHierarchy(hierarchy, entry)
            ' Assemble children: optional [All Tracks] first, then one folder per key.
            Dim allTracksOffset As Integer = If(includeAllTracks, 1, 0)
            Dim subFolders(sortedKeys.Count + allTracksOffset - 1) As FolderNode
            If includeAllTracks Then
                subFolders(0) = BuildAllTracksFolder(files)
            End If
            For i As Integer = 0 To sortedKeys.Count - 1
                Dim k As String = sortedKeys(i)
                Dim subFolder As FolderNode = BuildHierarchicalFolderNode(grouped(k), childHierarchy, leaf, includeAllTracks, albumGroupBy)
                subFolder.Name = k
                subFolders(i + allTracksOffset) = subFolder
            Next
            Return New FolderNode("", subFolders, files)
        End Function

        ' Tree-by-slash helper: a node in the in-memory slash tree. Children keyed by
        ' segment name (case-insensitive). Files attached directly to this node are
        ' tracks whose value ended exactly here (e.g. "World" with no further "/").
        Private NotInheritable Class SlashTreeNode
            Public Files As New List(Of String())
            Public Children As New Dictionary(Of String, SlashTreeNode)(StringComparer.OrdinalIgnoreCase)
        End Class

        ' Tree-by-slash: split each track's field value(s) on "/" into a nested folder
        ' tree (e.g. "World/Africa/West" → World → Africa → West). Multi-value ("; ")
        ' values produce independent slash-trees per value. At each leaf segment (no
        ' deeper "/"), recurse into the remaining hierarchy as if it were a normal
        ' grouping level. Intermediate (mixed) nodes show only segment children plus
        ' an optional [All Tracks] roll-up.
        Private Shared Function BuildTreeBySlashFolderNode(
                files As List(Of String()),
                fieldMDI As MetaDataIndex,
                restHierarchy() As HierarchyEntry,
                leaf As LeafMode,
                includeAllTracks As Boolean,
                albumGroupBy As Plugin.AlbumGroupField(),
                sortDescending As Boolean) As FolderNode
            Dim root As New SlashTreeNode()
            For Each tags As String() In files
                Dim raw As String = tags(fieldMDI)
                Dim values() As String
                If String.IsNullOrEmpty(raw) Then
                    values = New String() {Plugin.L("UnknownArtist")}
                Else
                    values = raw.Split(New String() {"; "}, StringSplitOptions.None)
                End If
                For Each v As String In values
                    Dim segments() As String = v.Split("/"c)
                    Dim cursor As SlashTreeNode = root
                    For Each seg As String In segments
                        Dim trimmed As String = seg.Trim()
                        If trimmed.Length = 0 Then Continue For
                        Dim child As SlashTreeNode = Nothing
                        If Not cursor.Children.TryGetValue(trimmed, child) Then
                            child = New SlashTreeNode()
                            cursor.Children(trimmed) = child
                        End If
                        cursor = child
                    Next
                    If cursor IsNot root Then cursor.Files.Add(tags)
                Next
            Next
            Return ConvertSlashTreeToFolderNode(root, restHierarchy, leaf, includeAllTracks, albumGroupBy, sortDescending)
        End Function

        Private Shared Function ConvertSlashTreeToFolderNode(
                node As SlashTreeNode,
                restHierarchy() As HierarchyEntry,
                leaf As LeafMode,
                includeAllTracks As Boolean,
                albumGroupBy As Plugin.AlbumGroupField(),
                sortDescending As Boolean) As FolderNode
            ' Pure leaf - no further slash children. Render rest of hierarchy on this
            ' segment's files directly.
            If node.Children.Count = 0 Then
                Dim leafF As FolderNode = BuildHierarchicalFolderNode(node.Files, restHierarchy, leaf, includeAllTracks, albumGroupBy)
                Return New FolderNode("", leafF.Folders, leafF.ChildFiles)
            End If
            ' Container - bubble all descendant files for the optional [All Tracks]
            ' roll-up, then list segment children alphabetically (sort honors entry's
            ' SortDescending).
            Dim allFiles As New List(Of String())
            CollectSlashTreeFiles(node, allFiles)
            Dim keys As New List(Of String)(node.Children.Keys)
            keys.Sort(StringComparer.CurrentCultureIgnoreCase)
            If sortDescending Then keys.Reverse()
            Dim subFolders As New List(Of FolderNode)
            If includeAllTracks Then subFolders.Add(BuildAllTracksFolder(allFiles))
            For Each k As String In keys
                Dim childFolder As FolderNode = ConvertSlashTreeToFolderNode(node.Children(k), restHierarchy, leaf, includeAllTracks, albumGroupBy, sortDescending)
                childFolder.Name = k
                subFolders.Add(childFolder)
            Next
            Return New FolderNode("", subFolders.ToArray(), allFiles)
        End Function

        ' Stamp Category onto every TemplateNode in the subtree. Used by Radio to mark
        ' all descendants so the leaf-emission path can pick the audioBroadcast UPnP class.
        Private Shared Sub SetCategoryRecursive(node As TemplateNode, category As ContainerCategory)
            If node Is Nothing Then Return
            node.Category = category
            If node.ChildNodes IsNot Nothing Then
                For Each child As TemplateNode In node.ChildNodes
                    SetCategoryRecursive(child, category)
                Next
            End If
        End Sub

        Private Shared Sub CollectSlashTreeFiles(node As SlashTreeNode, sink As List(Of String()))
            sink.AddRange(node.Files)
            For Each child As SlashTreeNode In node.Children.Values
                CollectSlashTreeFiles(child, sink)
            Next
        End Sub

        ' Group files by the current hierarchy level's field. Multi-value fields ("X; Y")
        ' produce one entry per value (the same file appears under multiple groups).
        ' BucketByLetter folds the value down to its first letter (A-Z) or "#" for
        ' anything that doesn't start with a letter (digits, symbols, empty).
        ' Empty values bucket under "Unknown Artist" (i18n string) unless BucketByLetter,
        ' in which case they go to "#".
        Private Shared Function GroupFilesByHierarchyEntry(files As List(Of String()), fieldMDI As MetaDataIndex, bucketByLetter As Boolean) As Dictionary(Of String, List(Of String()))
            Dim grouped As New Dictionary(Of String, List(Of String()))(StringComparer.OrdinalIgnoreCase)
            For Each tags As String() In files
                Dim rawValue As String = tags(fieldMDI)
                Dim values() As String
                If String.IsNullOrEmpty(rawValue) Then
                    values = New String() {Plugin.L("UnknownArtist")}
                Else
                    values = rawValue.Split(New String() {"; "}, StringSplitOptions.None)
                End If
                For Each v As String In values
                    Dim k As String = BuildGroupKey(v, bucketByLetter)
                    Dim lst As List(Of String()) = Nothing
                    If Not grouped.TryGetValue(k, lst) Then
                        lst = New List(Of String())
                        grouped(k) = lst
                    End If
                    lst.Add(tags)
                Next
            Next
            Return grouped
        End Function

        ' Single value → group key. BucketByLetter folds to uppercase first letter or "#".
        ' Otherwise returns the value itself (with empty → "Unknown Artist").
        Private Shared Function BuildGroupKey(value As String, bucketByLetter As Boolean) As String
            If bucketByLetter Then
                Dim trimmed As String = If(String.IsNullOrEmpty(value), "", value.TrimStart())
                If trimmed.Length = 0 Then Return "#"
                Dim firstChar As Char = Char.ToUpperInvariant(trimmed.Chars(0))
                Return If(Char.IsLetter(firstChar), firstChar.ToString(), "#")
            End If
            Return If(String.IsNullOrEmpty(value), Plugin.L("UnknownArtist"), value)
        End Function

        ' Alphabetical case-insensitive sort, optionally reversed.
        Private Shared Function SortHierarchyKeys(grouped As Dictionary(Of String, List(Of String())), descending As Boolean) As List(Of String)
            Dim keys As New List(Of String)(grouped.Keys)
            keys.Sort(StringComparer.CurrentCultureIgnoreCase)
            If descending Then keys.Reverse()
            Return keys
        End Function

        ' Compute the hierarchy passed to the recursive call. When the current level is
        ' BucketByLetter, the NEXT level re-uses the same field WITHOUT bucketing - so
        ' the alphabet bucket contains the actual artist/composer/etc. groups. Otherwise
        ' just drop the consumed entry.
        Private Shared Function NextLevelHierarchy(hierarchy() As HierarchyEntry, currentEntry As HierarchyEntry) As HierarchyEntry()
            Dim rest As HierarchyEntry() = hierarchy.Skip(1).ToArray()
            If currentEntry.BucketByLetter Then
                Return (New HierarchyEntry() {New HierarchyEntry(currentEntry.Field, False, currentEntry.SortDescending)}).Concat(rest).ToArray()
            End If
            Return rest
        End Function

        ' Build the "[All Tracks]" folder shown at each grouping level when
        ' IncludeAllTracks is on. Holds the full file list (a copy, sorted disc/track)
        ' so descending into it doesn't disturb the parent's order.
        Private Shared Function BuildAllTracksFolder(files As List(Of String())) As FolderNode
            Dim allTracks As New List(Of String())(files)
            allTracks.Sort(New AlbumFileComparer)
            Return New FolderNode(Plugin.L("AllTracks"), New FolderNode() {}, allTracks)
        End Function

        ' Walk the playlist FolderNode tree; for each leaf playlist with a non-trivial binding
        ' (hierarchy non-empty OR Leaf=AT), eagerly load its tracks and replace its flat
        ' structure with a hierarchical sub-tree per the binding. Playlists with the Default
        ' Playlist template (empty hierarchy + T leaf) are skipped - they keep today's lazy
        ' flat-tracks behaviour, no eager load cost.
        Private Shared Sub WirePlaylistBindings(ByRef folder As FolderNode)
            If folder.Folders IsNot Nothing Then
                For i As Integer = 0 To folder.Folders.Length - 1
                    WirePlaylistBindings(folder.Folders(i))
                Next
            End If
            ' Leaf-playlist guard: must have a real playlist URL, no children yet, not NowPlaying.
            If folder.Path Is Nothing OrElse folder.Path.Length = 0 OrElse folder.Path = "NowPlaying" Then Return
            If folder.Folders IsNot Nothing AndAlso folder.Folders.Length > 0 Then Return
            Dim fullName As String = mbApiInterface.Playlist_GetName(folder.Path)
            Dim binding As EndpointBinding = View.GetBinding("playlist:" & fullName)
            If binding Is Nothing OrElse binding.Paths Is Nothing OrElse binding.Paths.Length = 0 Then Return
            ' Single-path shortcut: empty hierarchy + T leaf is the "Default Tracks" template -
            ' today's lazy flat-track loader handles it without wiring. Skip to keep that cheap.
            If binding.Paths.Length = 1 Then
                Dim p As BrowsePath = binding.Paths(0)
                Dim hierarchyLen As Integer = If(p.Hierarchy Is Nothing, 0, p.Hierarchy.Length)
                If hierarchyLen = 0 AndAlso p.Leaf = LeafMode.T AndAlso Not p.IncludeAllTracks Then Return
            End If
            ' Multi-path or non-trivial single path - eagerly load the playlist's tracks.
            Dim filenames() As String = Nothing
            Try
                mbApiInterface.Playlist_QueryFilesEx(folder.Path, filenames)
            Catch
                Return
            End Try
            If filenames Is Nothing OrElse filenames.Length = 0 Then Return
            Dim files As New List(Of String())
            For Each fn As String In filenames
                files.Add(LookupOrLoadFile(fn))
            Next
            If binding.Paths.Length = 1 Then
                ' Single path: today's flat replacement. The leaf playlist's Folders becomes
                ' the path's hierarchical sub-tree directly (no extra wrapper level).
                Dim p As BrowsePath = binding.Paths(0)
                Dim structured As FolderNode = BuildHierarchicalFolderNode(files, p.Hierarchy, p.Leaf, p.IncludeAllTracks, p.AlbumGroupBy)
                folder.Folders = structured.Folders
                folder.ChildFiles = files
            Else
                ' Multi-path: wrap. The leaf playlist becomes a folder of named view-paths;
                ' each path is its own grouping of the same track set. Labels auto-generated.
                Dim subFolders(binding.Paths.Length - 1) As FolderNode
                For i As Integer = 0 To binding.Paths.Length - 1
                    Dim p As BrowsePath = binding.Paths(i)
                    Dim pathLabel As String = View.FormatBrowsePathShortDisplay(p)
                    Dim pathFolder As FolderNode = BuildHierarchicalFolderNode(files, p.Hierarchy, p.Leaf, p.IncludeAllTracks, p.AlbumGroupBy)
                    pathFolder.Name = pathLabel
                    subFolders(i) = pathFolder
                Next
                folder.Folders = subFolders
                folder.ChildFiles = files
            End If
        End Sub

        Private Shared Sub LoadPlaylistFolder(playlist As PlaylistFolderNode, ByRef folder As FolderNode)
            ' With FolderNode now a Class, array slots default to Nothing (used to
            ' default to empty structs). Allocate each child slot before recursing
            ' so the recursive call has a real object to mutate.
            folder.Folders = New FolderNode(playlist.Folders.Count - 1) {}
            For index As Integer = 0 To folder.Folders.Length - 1
                folder.Folders(index) = New FolderNode()
                LoadPlaylistFolder(playlist.Folders(index), folder.Folders(index))
            Next index
            folder.Path = playlist.Path
            folder.Name = playlist.Name
        End Sub

        ' Materialise this folder's children (if needed) then optionally recurse into
        ' grand-children. Populate-before-drill order matters because the drill loop
        ' indexes into folder.Folders - that array must exist before we walk it.
        '
        ' Three populate strategies, chosen by template shape:
        '   1. Playlist category - skipped here (LoadLibraryPlaylists handles it).
        '   2. Header-only template (every child has an explicit Name) - produce
        '      one empty FolderNode per child, matched by index to template.ChildNodes.
        '      Used by the legacy hardcoded sub-trees.
        '   3. Grouping template (template.Fields drives the grouping) - read
        '      sourceFiles, group by Fields, produce a sorted (or bucketed) folder list.
        Private Sub LoadNode(template As TemplateNode, ByRef folder As FolderNode, drillDown As Boolean)
            ' Lazy templates carry an "L:" path prefix and are populated on-demand by
            ' LazyBrowse - they intentionally have no Fields and no ChildNodes, which would
            ' crash PopulateByGrouping (fields(fields.Length - 1) NRE). Short-circuit them
            ' here so the eager root walk treats them as opaque leaf containers.
            If template.Path IsNot Nothing AndAlso template.Path.StartsWith("L:", StringComparison.Ordinal) Then
                Return
            End If
            If template.Category <> ContainerCategory.Playlist Then
                Dim needsPopulate As Boolean = (folder.Folders Is Nothing)
                Dim hasChildFiles As Boolean = (folder.ChildFiles IsNot Nothing)
                Dim hasGroupingFields As Boolean = (template.Fields IsNot Nothing)
                ' Skip populate when folder is already loaded, OR when folder has direct
                ' child files AND no grouping fields tell us what to do with them.
                If needsPopulate AndAlso (Not hasChildFiles OrElse hasGroupingFields) Then
                    If IsHeaderOnlyTemplate(template) Then
                        PopulateHeaderFolders(template, folder)
                    Else
                        PopulateByGrouping(template, folder)
                    End If
                End If
            End If
            If drillDown Then
                DrillIntoChildren(template, folder)
            End If
        End Sub

        ' Header-only = every child template has an explicit Name. Used by the legacy
        ' hardcoded Music sub-tree before it became binding-driven (each child was
        ' "Artists", "Albums", etc., as a labelled container with its own grouping
        ' fields). Returns False if ChildNodes is missing or any child has Name=Nothing
        ' (those need to be derived from grouping a parent file).
        Private Shared Function IsHeaderOnlyTemplate(template As TemplateNode) As Boolean
            If template.ChildNodes Is Nothing Then Return False
            For Each child As TemplateNode In template.ChildNodes
                If child.Name Is Nothing Then Return False
            Next
            Return True
        End Function

        ' Allocate one empty FolderNode per child template, naming each from the child's
        ' .Name. Used when every child template is explicitly labelled (header-only mode).
        Private Shared Sub PopulateHeaderFolders(template As TemplateNode, folder As FolderNode)
            folder.Folders = New FolderNode(template.ChildNodes.Length - 1) {}
            For index As Integer = 0 To template.ChildNodes.Length - 1
                folder.Folders(index) = New FolderNode(template.ChildNodes(index).Name)
            Next index
        End Sub

        ' Group sourceFiles by template.Fields, produce a sorted or letter-bucketed
        ' folder array, and stash it in folder.Folders. The grouping pipeline:
        '   PrecomputeAlbumArtistAndAlbum → side-effect to fill the synthetic field if used
        '   GroupSourceFiles              → file→folder buckets keyed by Fields tuple
        '   ShouldBucket                  → either sort linearly or letter-bucket the list
        Private Shared Sub PopulateByGrouping(template As TemplateNode, folder As FolderNode)
            Dim sourceFiles As List(Of String()) = If(folder.ChildFiles, musicFiles)
            Dim fields() As MetaDataIndex = template.Fields
            Dim lastField As MetaDataIndex = fields(fields.Length - 1)
            PrecomputeAlbumArtistAndAlbumIfNeeded(sourceFiles, fields)
            Dim lookupFolders As List(Of FolderNode) = GroupSourceFiles(sourceFiles, fields, lastField)
            If ShouldBucket(template, lookupFolders, lastField) Then
                CreateBucket(lookupFolders, sourceFiles, (lastField <> MetaDataIndex.Genre), template.IncludeAllTracks, folder)
            Else
                folder.Folders = ArrangeSortedFolders(lookupFolders, lastField, template.IncludeAllTracks, sourceFiles)
            End If
        End Sub

        ' Synthetic AlbumArtistAndAlbum field is computed lazily - only when a template
        ' actually uses it for grouping. Fills the position MetaDataIndex.AlbumArtistAndAlbum
        ' on each source file with "<AlbumArtist> - <Album>" (empty if no album).
        Private Shared Sub PrecomputeAlbumArtistAndAlbumIfNeeded(sourceFiles As List(Of String()), fields() As MetaDataIndex)
            Dim needed As Boolean = False
            For Each f As MetaDataIndex In fields
                If f = MetaDataIndex.AlbumArtistAndAlbum Then
                    needed = True
                    Exit For
                End If
            Next
            If Not needed Then Return
            For Each tags As String() In sourceFiles
                If tags(MetaDataIndex.Album).Length = 0 Then
                    tags(MetaDataIndex.AlbumArtistAndAlbum) = ""
                Else
                    tags(MetaDataIndex.AlbumArtistAndAlbum) = tags(MetaDataIndex.AlbumArtist) & " - " & tags(MetaDataIndex.Album)
                End If
            Next
        End Sub

        ' Group source files into FolderNodes keyed by the Fields tuple.
        '   Single-field path:   one folder per distinct field value.
        '   Multi-field path:    one folder per distinct (fieldA, fieldB, ...) tuple.
        '   Genre-only special:  files whose Genre tag contains "; " get split - the
        '                        same track appears under each genre separately.
        ' Files with empty values at any field level are skipped (would group under "").
        Private Shared Function GroupSourceFiles(sourceFiles As List(Of String()), fields() As MetaDataIndex, lastField As MetaDataIndex) As List(Of FolderNode)
            Dim key As New StringBuilder(256)
            Dim lookup As New Dictionary(Of String, List(Of String()))(StringComparer.OrdinalIgnoreCase)
            Dim lookupFolders As New List(Of FolderNode)
            Dim isSingleGenreField As Boolean = (fields.Length = 1 AndAlso fields(0) = MetaDataIndex.Genre)
            For Each tags As String() In sourceFiles
                Dim multiGenre As Boolean = isSingleGenreField AndAlso tags(MetaDataIndex.Genre).IndexOf("; ", StringComparison.Ordinal) >= 0
                If multiGenre Then
                    For Each genre As String In tags(MetaDataIndex.Genre).Split(New String() {"; "}, StringSplitOptions.None)
                        AddFileToGroup(lookup, lookupFolders, genre, genre, tags)
                    Next
                ElseIf fields.Length = 1 Then
                    Dim folderName As String = tags(fields(0))
                    If folderName.Length > 0 Then
                        AddFileToGroup(lookup, lookupFolders, folderName, folderName, tags)
                    End If
                Else
                    key.Length = 0
                    Dim skip As Boolean = False
                    For fieldIndex As Integer = 0 To fields.Length - 1
                        If tags(fields(fieldIndex)).Length = 0 Then
                            skip = True
                            Exit For
                        End If
                        If fieldIndex > 0 Then key.Append(ChrW(0))
                        key.Append(tags(fields(fieldIndex)))
                    Next
                    If Not skip Then
                        AddFileToGroup(lookup, lookupFolders, key.ToString(), tags(lastField), tags)
                    End If
                End If
            Next
            Return lookupFolders
        End Function

        ' Add a file to its group, creating the group's FolderNode the first time.
        ' lookup/lookupFolders stay in sync - lookup keyed by groupKey, lookupFolders
        ' holds the same FolderNode references for later sorting/bucketing.
        Private Shared Sub AddFileToGroup(lookup As Dictionary(Of String, List(Of String())), lookupFolders As List(Of FolderNode), groupKey As String, displayName As String, tags() As String)
            Dim files As List(Of String()) = Nothing
            If Not lookup.TryGetValue(groupKey, files) Then
                files = New List(Of String())
                lookup.Add(groupKey, files)
                lookupFolders.Add(New FolderNode(displayName, files))
            End If
            files.Add(tags)
        End Sub

        ' Letter-bucket when there are too many distinct values for a flat list, EXCEPT
        ' Year (4-digit values are useless to bucket by first letter) - Year always
        ' renders flat. The threshold + master toggle come from Settings.
        Private Shared Function ShouldBucket(template As TemplateNode, lookupFolders As List(Of FolderNode), lastField As MetaDataIndex) As Boolean
            If lastField = MetaDataIndex.Year Then Return False
            If Not Settings.BucketNodes Then Return False
            Return lookupFolders.Count > Settings.BucketTrigger
        End Function

        ' Linear flat arrangement (the non-bucket case): sort lookupFolders, optionally
        ' prepend an [All Tracks] folder containing the full sourceFiles list, return
        ' the result as a fresh FolderNode array. Artist/AlbumArtist/Album use the
        ' "ignore leading articles" comparer; everything else uses literal name sort.
        Private Shared Function ArrangeSortedFolders(lookupFolders As List(Of FolderNode), lastField As MetaDataIndex, includeAllTracks As Boolean, sourceFiles As List(Of String())) As FolderNode()
            Select Case lastField
                Case MetaDataIndex.Artist, MetaDataIndex.AlbumArtist, MetaDataIndex.Album
                    lookupFolders.Sort(New FolderPrefixedNameComparer)
                Case Else
                    lookupFolders.Sort(New FolderNameComparer)
            End Select
            Dim totalCount As Integer = lookupFolders.Count + If(includeAllTracks, 1, 0)
            Dim folders() As FolderNode = New FolderNode(totalCount - 1) {}
            Dim offset As Integer = 0
            If includeAllTracks Then
                folders(0) = New FolderNode("[All Tracks]", sourceFiles)
                offset = 1
            End If
            For index As Integer = 0 To lookupFolders.Count - 1
                folders(offset) = lookupFolders(index)
                offset += 1
            Next
            Return folders
        End Function

        ' Recurse into populated children. Bounded by Min(childNodes.Length,
        ' folder.Folders.Length) so a mismatch can't index out of range - extra slots
        ' on either side are silently ignored.
        Private Sub DrillIntoChildren(template As TemplateNode, folder As FolderNode)
            Dim childNodes() As TemplateNode = template.ChildNodes
            If childNodes Is Nothing OrElse folder.Folders Is Nothing Then Return
            Dim limit As Integer = Math.Min(childNodes.Length, folder.Folders.Length)
            For index As Integer = 0 To limit - 1
                LoadNode(childNodes(index), folder.Folders(index), False)
            Next
        End Sub

        Private Shared Sub CreateBucket(sourceFolders As List(Of FolderNode), sourceFiles As List(Of String()), useNameComparer As Boolean, includeAllTracks As Boolean, ByRef folder As FolderNode)
            Dim bucket As New Dictionary(Of Char, List(Of FolderNode))
            For index As Integer = 0 To sourceFolders.Count - 1
                Dim c As Char = If(useNameComparer, GetNameBucket(sourceFolders(index).Name), GetBucket(sourceFolders(index).Name))
                Dim bucketFolders As List(Of FolderNode)
                If Not bucket.TryGetValue(c, bucketFolders) Then
                    bucketFolders = New List(Of FolderNode)
                    bucket.Add(c, bucketFolders)
                End If
                bucketFolders.Add(sourceFolders(index))
            Next index
            Dim letters As New List(Of String)
            For Each c As Char In bucket.Keys
                letters.Add(c)
            Next c
            letters.Sort(StringComparer.CurrentCultureIgnoreCase)
            Dim folders() As FolderNode = New FolderNode(letters.Count - If(includeAllTracks, 0, 1)) {}
            folder.IsBucket = True
            folder.Folders = folders
            Dim offset As Integer = 0
            If includeAllTracks Then
                folders(0) = New FolderNode("[All Tracks]", sourceFiles)
                offset = 1
            End If
            For Each letter As String In letters
                Dim folderFiles As New List(Of String())
                Dim bucketFolders As List(Of FolderNode) = bucket(letter.Chars(0))
                For Each bucketFolder As FolderNode In bucketFolders
                    folderFiles.AddRange(bucketFolder.ChildFiles)
                Next bucketFolder
                If useNameComparer Then
                    bucketFolders.Sort(New FolderPrefixedNameComparer)
                Else
                    bucketFolders.Sort(New FolderNameComparer)
                End If
                folders(offset) = New FolderNode(letter, bucketFolders.ToArray(), folderFiles)
                offset += 1
            Next letter
        End Sub

        Public Function TryGetFileInfo(objectId As String, ByRef url As String, ByRef duration As TimeSpan) As Boolean
            If Not fileLookupLoaded Then
                LoadLibrary()
            End If
            If TryResolveFileInfo(objectId, url, duration) Then Return True
            ' Miss: a DIRECT play reaches GetFile with no prior Browse - e.g. BubbleUPnP's
            ' "Recently Played" list (or a cast) requests the track by its id straight after a
            ' restart. The lazy endpoints (podcast/inbox/audiobook/radio) only hash their tracks
            ' into fileLookup when that endpoint is browsed, so the id is unknown and GetFile
            ' 404s ("Bad id"). Force-load the bounded ones once and retry before giving up.
            EnsureDirectAccessEndpoints()
            Return TryResolveFileInfo(objectId, url, duration)
        End Function

        Private Function TryResolveFileInfo(objectId As String, ByRef url As String, ByRef duration As TimeSpan) As Boolean
            Dim tags() As String = Nothing
            SyncLock fileLookup
                If Not fileLookup.TryGetValue(objectId, tags) Then Return False
                url = tags(MetaDataIndex.Url)
                Dim durationValue As Long
                If Long.TryParse(tags(MetaDataIndex.Duration), durationValue) Then
                    duration = New TimeSpan(durationValue)
                End If
            End SyncLock
            Return True
        End Function

        ' Force-load the bounded lazy endpoints so their tracks are present in fileLookup for a
        ' DIRECT id access that skipped Browse (see TryGetFileInfo). Each Ensure* is a one-time
        ' fetch guarded by its loaded flag, so this is a no-op once the endpoint has been browsed.
        ' Music is deliberately NOT force-loaded: it is not a bounded list, its tracks enter
        ' fileLookup only as they are browsed. (audiobook + inbox share one partition pass.)
        Private Shared Sub EnsureDirectAccessEndpoints()
            EnsureLazyEndpointInMemory("podcast")
            EnsureLazyEndpointInMemory("audiobook")
            EnsureLazyEndpointInMemory("inbox")
            EnsureLazyEndpointInMemory("radio")
        End Sub

        Public Function TryGetThumbnailFile(objectId As String, ByRef pictureUrl As String) As Boolean
            Dim tags() As String = Nothing
            SyncLock fileLookup
                If Not fileLookup.TryGetValue(objectId, tags) Then
                    Return False
                Else
                    Dim url As String = tags(MetaDataIndex.Url)
                    If url.EndsWith("#"c) Then
                        ' remove track from virtual url
                        url = url.Substring(0, url.LastIndexOf("#"c, url.Length - 2))
                    End If
                    pictureUrl = mbApiInterface.Library_GetArtworkUrl(url, -1)
                    If pictureUrl Is Nothing Then
                        Return False
                    End If
                    Return True
                End If
            End SyncLock
        End Function

        ' Build (or refresh) the album index used by Search responses for class musicAlbum.
        ' Groups musicFiles by AlbumArtist+Album. Cheap: O(N) over musicFiles. Rebuilt only
        ' when musicFiles count changes (a proxy for library reload).
        Private Shared Sub BuildSearchAlbumIndex()
            Dim currentCount As Integer = If(musicFiles Is Nothing, 0, musicFiles.Count)
            If searchAlbumsBuiltCount = currentCount Then Return
            searchAlbumKeys.Clear()
            searchAlbumTracks.Clear()
            If musicFiles IsNot Nothing Then
                For Each tags As String() In musicFiles
                    Dim album As String = tags(MetaDataIndex.Album)
                    If String.IsNullOrEmpty(album) Then Continue For
                    Dim key As String = tags(MetaDataIndex.AlbumArtist) & "|" & album
                    Dim list As List(Of String()) = Nothing
                    If Not searchAlbumTracks.TryGetValue(key, list) Then
                        list = New List(Of String())
                        searchAlbumTracks(key) = list
                        searchAlbumKeys.Add(key)
                    End If
                    list.Add(tags)
                Next
                For Each list As List(Of String()) In searchAlbumTracks.Values
                    list.Sort(New AlbumFileComparer)
                Next
            End If
            searchAlbumsBuiltCount = currentCount
        End Sub

        ' True if the search criteria is asking the server for album CONTAINERS
        ' (upnp:class = / derivedfrom "object.container.album.musicAlbum"), as opposed to
        ' tracks. BubbleUPnP's "Random Albums" virtual folder fires this query.
        Private Shared Function IsAlbumClassQuery(searchCriteria As String) As Boolean
            If String.IsNullOrEmpty(searchCriteria) Then Return False
            Dim hasAlbum As Boolean = searchCriteria.IndexOf("object.container.album.musicAlbum", StringComparison.OrdinalIgnoreCase) >= 0
            Dim hasTrack As Boolean = searchCriteria.IndexOf("object.item.audioItem", StringComparison.OrdinalIgnoreCase) >= 0
            ' If both classes are mentioned (e.g. "and (upnp:artist = X)") prefer the more
            ' specific request - but the typical Random-Albums shape is album-only.
            Return hasAlbum AndAlso Not hasTrack
        End Function

        ' Maximum DIDL entries per search-result section. Hardcoded for the initial
        ' minimal implementation; will move to Settings (configurable from the UI)
        ' once we've observed the real BubbleUPnP behaviour and tuned a sensible
        ' default.
        ' Result cache for one search, keyed by the generated SmartPlaylist query. A client
        ' pages through a single search, so this turns N page requests into ONE MB query.
        ' Deliberately tiny - it exists to serve the paging burst of the search in progress,
        ' not to be a long-lived index. Cleared wholesale when it grows past a few searches,
        ' which is also what keeps a library edit from being served stale for long.
        Private Const LazySearchCacheMax As Integer = 8
        Private Shared ReadOnly lazySearchUrlCache As New Dictionary(Of String, String())(StringComparer.Ordinal)

        Private Shared Function GetCachedSearchUrls(query As String, logLabel As String) As String()
            SyncLock lazySearchUrlCache
                Dim hit() As String = Nothing
                If lazySearchUrlCache.TryGetValue(query, hit) Then Return hit
            End SyncLock
            Dim urls() As String = Nothing
            LazyQueryFilesEx(query, urls, logLabel)
            If urls Is Nothing Then urls = New String() {}
            SyncLock lazySearchUrlCache
                If lazySearchUrlCache.Count >= LazySearchCacheMax Then lazySearchUrlCache.Clear()
                lazySearchUrlCache(query) = urls
            End SyncLock
            Return urls
        End Function

        ' New lazy-aware search path. Handles BubbleUPnP's standard global-search
        ' queries - currently just tracks-by-title and albums-by-title, both built
        ' from the same MB query (Title contains "TERM") differing only in DIDL
        ' aggregation. Returns False when the criteria doesn't match a pattern we
        ' implement, so the caller falls through to legacy / empty handling.
        '
        ' Scoping: containerId is the *post-substitution* value from
        ' ContentDirectoryService.ResolveSearchContainer. If it starts with
        ' "L:music" we parse its filter chain and AND those conditions with the
        ' Title-contains predicate. For any other container (including non-music
        ' lazy endpoints) we currently degrade to global-music search; per-
        ' endpoint scope support is a follow-up.
        Private Function HandleLazySearch(headers As Dictionary(Of String, String), containerId As String, searchCriteria As String, filter As String, startingIndex As Integer, requestedCount As Integer, ByRef result As String, ByRef numberReturned As String, ByRef totalMatches As String) As Boolean
            Dim term As String = ExtractTitleContainsTerm(searchCriteria)
            Dim artistTerm As String = ExtractArtistContainsTerm(searchCriteria)
            If String.IsNullOrEmpty(term) AndAlso String.IsNullOrEmpty(artistTerm) Then Return False
            Dim wantsAlbums As Boolean = searchCriteria.IndexOf("musicAlbum", StringComparison.OrdinalIgnoreCase) >= 0
            Dim wantsTracks As Boolean = (Not wantsAlbums) AndAlso _
                (searchCriteria.IndexOf("audioItem", StringComparison.OrdinalIgnoreCase) >= 0 OrElse _
                 searchCriteria.IndexOf("musicTrack", StringComparison.OrdinalIgnoreCase) >= 0)
            If Not wantsAlbums AndAlso Not wantsTracks Then Return False
            ' Build the SmartPlaylist: scope conditions (if any) + Title contains.
            '
            ' Scope-parsing accepts any L:* container whose endpoint uses MB's
            ' Library_QueryFilesEx data path:
            '   • L:music…              - no base conditions, level filters only.
            '   • L:filter:<name>…      - base conditions from the .xautopf file +
            '                              level filters drilled into by the user.
            ' For in-memory endpoints (podcast/audiobook/radio/inbox) the data
            ' isn't reachable via SmartPlaylist queries - we silently drop their
            ' scope and search music globally instead. Honest alternative would be
            ' "no results when searching from podcast" but that's worse UX.
            Dim scopeFilters As New List(Of LazyFilter)
            Dim baseConditions As String = ""
            If Not String.IsNullOrEmpty(containerId) AndAlso containerId.StartsWith("L:", StringComparison.OrdinalIgnoreCase) Then
                Dim ep As String = Nothing
                Dim pIdx As Integer
                Dim parsed As List(Of LazyFilter) = Nothing
                If TryParseLazyId(containerId, ep, pIdx, parsed) Then
                    Dim epLower As String = If(ep, "").ToLowerInvariant()
                    Dim isMusic As Boolean = (epLower = "music")
                    Dim isFilter As Boolean = epLower.StartsWith("filter:", StringComparison.OrdinalIgnoreCase)
                    If isMusic OrElse isFilter Then
                        For Each f As LazyFilter In RealFiltersOnly(parsed)
                            scopeFilters.Add(f)
                        Next
                        If isFilter Then
                            baseConditions = GetFilterBaseConditions(ep.Substring("filter:".Length))
                        End If
                    End If
                End If
            End If
            Dim sb As New StringBuilder(512)
            sb.Append("<SmartPlaylist><Source Type=""1""><Conditions CombineMethod=""All"">")
            If baseConditions.Length > 0 Then sb.Append(baseConditions)
            For Each f As LazyFilter In scopeFilters
                If LazyFieldNeedsBoundaryAwareCompare(f.Field) Then
                    sb.Append(BuildMultiValueAwareCondition(f.Field, f.Value))
                Else
                    sb.Append("<Condition Field=""").Append(f.Field).Append(""" Comparison=""Is"" Value=""").Append(XmlAttributeEscape(f.Value)).Append(""" />")
                End If
            Next
            ' For musicAlbum-class queries `dc:title` refers to the ALBUM's title, not a
            ' track title. Filter on the Album field so search-for-album-named-X actually
            ' finds albums named X (not albums that happen to contain a track named X).
            ' For audioItem/musicTrack queries `dc:title` is the track title.
            Dim titleFieldName As String = If(wantsAlbums, "Album", "Title")
            If term.Length > 0 Then
                sb.Append("<Condition Field=""").Append(titleFieldName).Append(""" Comparison=""Contains"" Value=""").Append(XmlAttributeEscape(term)).Append(""" />")
            End If
            ' Artist search. Matches Artist OR AlbumArtist: a compilation's tracks carry the
            ' performer in Artist while the album is filed under AlbumArtist, so testing only
            ' one silently loses half the matches. Same nested-Or shape as the multi-value
            ' conditions built elsewhere, since MB has no cross-field OR at the top level.
            If artistTerm.Length > 0 Then
                Dim esc As String = XmlAttributeEscape(artistTerm)
                sb.Append("<Condition Field=""Artist"" Comparison=""Contains"" Value=""").Append(esc).Append(""">")
                sb.Append("<Or CombineMethod=""Any"">")
                sb.Append("<Condition Field=""AlbumArtist"" Comparison=""Contains"" Value=""").Append(esc).Append(""" />")
                sb.Append("</Or>")
                sb.Append("</Condition>")
            End If
            sb.Append("</Conditions></Source></SmartPlaylist>")
            Dim query As String = sb.ToString()
            Dim searchedBy As String = If(artistTerm.Length > 0 AndAlso term.Length = 0, "by-artist", "by-title")
            ' Cached per query: the client pages through one search, and re-running the MB
            ' query for every page cost ~250ms each on a 2800-hit artist search - the whole
            ' reason search felt slow. Same query text => same results, so the first page pays
            ' and the rest are free.
            Dim urls() As String = GetCachedSearchUrls(query, "Search " & If(wantsAlbums, "albums-", "tracks-") & searchedBy & " term=" & If(term.Length > 0, term, artistTerm))
            ' UPnP: RequestedCount 0 means "all from startingIndex".
            Dim pageSize As Integer = If(requestedCount <= 0, Integer.MaxValue, requestedCount)
            If startingIndex < 0 Then startingIndex = 0
            Dim host As String
            Dim hostUrl As String = If(Not headers.TryGetValue("host", host), PrimaryHostUrl, "http://" & host)
            Dim text As New StringBuilder(8192)
            Dim filterSet As HashSet(Of String) = Nothing
            If filter <> "*" Then
                filterSet = New HashSet(Of String)(filter.Split(New Char() {","c}, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
            End If
            Dim xmlSettings As New XmlWriterSettings With {.OmitXmlDeclaration = True, .Indent = False}
            Dim emitted As Integer = 0
            Dim totalCount As Integer = 0
            Using writer As XmlWriter = XmlWriter.Create(text, xmlSettings)
                writer.WriteStartElement("DIDL-Lite", "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/")
                writer.WriteAttributeString("xmlns", "dc", Nothing, "http://purl.org/dc/elements/1.1/")
                writer.WriteAttributeString("xmlns", "upnp", Nothing, "urn:schemas-upnp-org:metadata-1-0/upnp/")
                writer.WriteAttributeString("xmlns", "pv", Nothing, "http://www.pv.com/pvns/")
                If wantsAlbums Then
                    ' Aggregate matched tracks by (AlbumArtist, Album) and emit one
                    ' musicAlbum container per distinct album.
                    Dim albumGroups As New Dictionary(Of String, List(Of String()))(StringComparer.OrdinalIgnoreCase)
                    Dim albumOrder As New List(Of String)
                    SyncLock fileLookup
                        If urls IsNot Nothing Then
                            For Each url As String In urls
                                Dim tags() As String = LoadFile(url)
                                If tags Is Nothing Then Continue For
                                Dim album As String = tags(MetaDataIndex.Album)
                                If String.IsNullOrEmpty(album) Then Continue For
                                Dim key As String = tags(MetaDataIndex.AlbumArtist) & "|" & album
                                Dim list As List(Of String()) = Nothing
                                If Not albumGroups.TryGetValue(key, list) Then
                                    list = New List(Of String())
                                    albumGroups(key) = list
                                    albumOrder.Add(key)
                                End If
                                list.Add(tags)
                            Next
                        End If
                    End SyncLock
                    totalCount = albumOrder.Count
                    ' Serve the REQUESTED page. This used to always emit the first N and report
                    ' totalMatches = every match, so a client asking for items N.. got items 0..
                    ' again and paged forever, re-running the query each time.
                    Dim endIdx As Integer = Math.Min(startingIndex + pageSize - 1, totalCount - 1)
                    ' A fresh search (page 0) resets the album tap-through cache; later pages add
                    ' to it, so an id handed out on an earlier page keeps working.
                    If startingIndex = 0 Then
                        SyncLock searchAlbumResultTracks
                            searchAlbumResultTracks.Clear()
                        End SyncLock
                    End If
                    For i As Integer = startingIndex To endIdx
                        Dim key As String = albumOrder(i)
                        Dim tracks As List(Of String()) = albumGroups(key)
                        Dim title As String = tracks(0)(MetaDataIndex.Album)
                        Dim albumId As String = "Ssrch_alb_" & i.ToString()
                        ' Remember (id → tracks) so Browse on this synthetic ID can serve
                        ' the album's content when the user taps it in BubbleUPnP's results.
                        SyncLock searchAlbumResultTracks
                            searchAlbumResultTracks(albumId) = tracks
                        End SyncLock
                        WriteContainerDIDL(writer, hostUrl, filterSet, albumId, "0", tracks.Count.ToString(), title, "object.container.album.musicAlbum", tracks)
                        emitted += 1
                    Next
                Else
                    ' Load ONLY the requested page's tags - the whole point of paging. Loading
                    ' all 2800 hits per page was the second half of the slowness.
                    totalCount = If(urls Is Nothing, 0, urls.Length)
                    Dim endIdx As Integer = Math.Min(startingIndex + pageSize - 1, totalCount - 1)
                    Dim slice As New List(Of String())
                    SyncLock fileLookup
                        For i As Integer = startingIndex To endIdx
                            Dim tags() As String = LoadFile(urls(i))
                            If tags IsNot Nothing Then slice.Add(tags)
                        Next
                    End SyncLock
                    emitted = WriteAudioFilesDIDL(writer, hostUrl, filterSet, "object.item.audioItem.musicTrack", "0", slice, 0, slice.Count)
                End If
                writer.WriteEndElement()
            End Using
            numberReturned = emitted.ToString()
            totalMatches = totalCount.ToString()
            result = text.ToString()
            Return True
        End Function

        ' Class-ONLY search - a bare class predicate with NO "dc:title contains" term.
        ' BubbleUPnP's "Random Tracks" and "Random Albums" virtual folders fire these:
        '   upnp:class derivedfrom "object.item.audioItem"    → random tracks
        '   upnp:class = "object.container.album.musicAlbum"   → random albums
        ' They PAGE at random offsets (startingIndex/requestedCount), so - unlike the title
        ' search above, which caps at the first N - this MUST honour the requested slice and
        ' report an accurate totalMatches, or the client picks an offset we never serve and
        ' shows nothing. Data comes from the lazy MB query path: the eager `musicFiles` list is
        ' empty under the lazy design, which is exactly why the legacy fall-through returned
        ' zero. Returns False when the criteria isn't a class-only query we serve so the caller
        ' falls through to the legacy handler.
        ' The endpoint a class-only ("random") Search draws from.
        '
        ' A control point's Random Tracks / Random Albums folder is CLIENT-side: it fires a bare
        ' class query with no container, so there is nothing in the request that says "draw from
        ' my jazz filter". Settings.RandomSourceFilter is how the user says it here instead -
        ' empty = the whole music library, otherwise a filter basename applied as "filter:<name>",
        ' which LazyBuildFilterQueryFor turns into that .xautopf's conditions.
        '
        ' scopedEndpoint = a container scope that genuinely NARROWS the search (a filter, or music
        ' plus drill filters) - that wins, because the request already said which music. Pass
        ' Nothing for "no real scope", which includes a bare "music" (see the caller: the root
        ' container is substituted with L:music upstream, so bare music is NOT a client choice).
        Friend Shared Function RandomSourceEndpoint(scopedEndpoint As String) As String
            If Not String.IsNullOrEmpty(scopedEndpoint) Then Return scopedEndpoint
            Dim configured As String = If(Settings.RandomSourceFilter, "").Trim()
            If configured.Length = 0 Then Return "music"
            ' A filter the user picked then deleted/renamed on disk would yield an empty condition
            ' block, and LazyBuildFilterQueryFor would silently fall back to the whole library.
            ' Falling back explicitly keeps that behaviour, but makes it visible in the log.
            If GetFilterBaseConditions(configured).Length = 0 Then
                LogInformation("RandomSource", "configured filter """ & configured & """ has no conditions (missing or empty .xautopf) - drawing from the whole library")
                Return "music"
            End If
            Return "filter:" & configured
        End Function

        Private Function HandleClassOnlySearch(headers As Dictionary(Of String, String), containerId As String, searchCriteria As String, filter As String, startingIndex As Integer, requestedCount As Integer, ByRef result As String, ByRef numberReturned As String, ByRef totalMatches As String) As Boolean
            If String.IsNullOrEmpty(searchCriteria) Then Return False
            ' Only a query that genuinely asks for EVERYTHING of a class lands here. Any field
            ' predicate at all (title, artist, creator, genre, …) is HandleLazySearch's job -
            ' see HasFieldPredicate for why this must be the general test and not just dc:title.
            If HasFieldPredicate(searchCriteria) Then Return False
            Dim wantsAlbums As Boolean = IsAlbumClassQuery(searchCriteria)
            Dim wantsTracks As Boolean = (Not wantsAlbums) AndAlso searchCriteria.IndexOf("audioItem", StringComparison.OrdinalIgnoreCase) >= 0
            If Not wantsAlbums AndAlso Not wantsTracks Then Return False
            ' Scope: only a music / filter container exposes a queryable filter chain. Any other
            ' container (incl. in-memory endpoints) degrades to a global music search - same
            ' policy as HandleLazySearch, since podcast/inbox/etc. aren't SmartPlaylist-queryable.
            Dim scopeFilters As New List(Of LazyFilter)
            Dim scopedEndpoint As String = Nothing
            If Not String.IsNullOrEmpty(containerId) AndAlso containerId.StartsWith("L:", StringComparison.OrdinalIgnoreCase) Then
                Dim ep As String = Nothing
                Dim pIdx As Integer
                Dim parsed As List(Of LazyFilter) = Nothing
                If TryParseLazyId(containerId, ep, pIdx, parsed) Then
                    Dim epLower As String = If(ep, "").ToLowerInvariant()
                    If epLower = "music" OrElse epLower.StartsWith("filter:", StringComparison.OrdinalIgnoreCase) Then
                        ' Keep the endpoint, not just its level filters: for "L:filter:<name>" the
                        ' filter's .xautopf conditions live in the ENDPOINT (the name merges into it,
                        ' leaving parsed empty), so dropping it searched the whole library instead of
                        ' the filter. Passing it to the query builders below applies those conditions.
                        scopedEndpoint = ep
                        For Each f As LazyFilter In RealFiltersOnly(parsed)
                            scopeFilters.Add(f)
                        Next
                    End If
                End If
            End If
            ' Where this random pick draws from. A container scope wins ONLY when it actually
            ' NARROWS the search (a filter, or music plus drill filters).
            ' ⚠ CLAUDE: a bare "music" scope must NOT count as a deliberate choice. The root
            ' container "0" is substituted with "L:music" upstream (ContentDirectoryService.
            ' ResolveSearchContainer), and "0" is exactly what a client's Random folder sends -
            ' so treating bare music as an explicit scope makes Settings.RandomSourceFilter dead
            ' on arrival, which is the bug this shipped with on 2026-07-29. Bare music means
            ' "the whole library", which is the case the setting exists to redirect.
            Dim narrowedByClient As Boolean = scopedEndpoint IsNot Nothing AndAlso _
                (scopeFilters.Count > 0 OrElse scopedEndpoint.StartsWith("filter:", StringComparison.OrdinalIgnoreCase))
            Dim sourceEndpoint As String = RandomSourceEndpoint(If(narrowedByClient, scopedEndpoint, Nothing))
            Dim host As String
            Dim hostUrl As String = If(Not headers.TryGetValue("host", host), PrimaryHostUrl, "http://" & host)
            Dim filterSet As HashSet(Of String) = Nothing
            If filter <> "*" Then
                filterSet = New HashSet(Of String)(filter.Split(New Char() {","c}, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
            End If
            Dim text As New StringBuilder(16384)
            Dim xmlSettings As New XmlWriterSettings With {.OmitXmlDeclaration = True, .Indent = False}
            Dim emitted As Integer = 0
            Dim total As Integer = 0
            Using writer As XmlWriter = XmlWriter.Create(text, xmlSettings)
                writer.WriteStartElement("DIDL-Lite", "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/")
                writer.WriteAttributeString("xmlns", "dc", Nothing, "http://purl.org/dc/elements/1.1/")
                writer.WriteAttributeString("xmlns", "upnp", Nothing, "urn:schemas-upnp-org:metadata-1-0/upnp/")
                writer.WriteAttributeString("xmlns", "pv", Nothing, "http://www.pv.com/pvns/")
                If wantsAlbums Then
                    ' Album LIST via the lazy album grouping (cached, library-wide the first time).
                    Dim musicBinding As EndpointBinding = View.GetBinding("music")
                    Dim groupBy As Plugin.AlbumGroupField() = Nothing
                    If musicBinding IsNot Nothing AndAlso musicBinding.Paths IsNot Nothing AndAlso musicBinding.Paths.Length > 0 Then
                        groupBy = musicBinding.Paths(0).AlbumGroupBy
                    End If
                    Dim albums As List(Of LazyAlbumEntry) = SortLazyAlbums(GetLazyAlbumsForFilterIn(sourceEndpoint, scopeFilters, groupBy), groupBy)
                    total = albums.Count
                    ' Fresh search (page 0) resets the tap-through cache; later pages accumulate.
                    If startingIndex = 0 Then
                        SyncLock searchAlbumResultTracks
                            searchAlbumResultTracks.Clear()
                        End SyncLock
                    End If
                    Dim endIdx As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                    For i As Integer = startingIndex To endIdx
                        Dim a As LazyAlbumEntry = albums(i)
                        ' Tracks for THIS album = scope + the album's identity fields. Bounded to one
                        ' album, so the per-album query is cheap even though the list scan was global.
                        ' Cached under a stable "Ssrch_alb_<i>" id so the existing Browse route
                        ' (searchAlbumResultTracks) serves the tracks when the user taps the album.
                        Dim albumFilters As New List(Of LazyFilter)(scopeFilters)
                        albumFilters.AddRange(a.Key)
                        Dim tracks As List(Of String()) = GetLazyTracksForFilterIn(sourceEndpoint, albumFilters)
                        Dim albumId As String = "Ssrch_alb_" & i.ToString()
                        SyncLock searchAlbumResultTracks
                            searchAlbumResultTracks(albumId) = tracks
                        End SyncLock
                        WriteContainerDIDL(writer, hostUrl, filterSet, albumId, "0", tracks.Count.ToString(), a.Title, "object.container.album.musicAlbum", tracks)
                        emitted += 1
                    Next
                Else
                    ' Track LIST: the scope's whole track set, sliced to the requested page.
                    Dim query As String = LazyBuildFilterQueryFor(sourceEndpoint, scopeFilters)
                    Dim urls() As String = Nothing
                    LazyQueryFilesEx(query, urls, "Search class-only tracks")
                    total = If(urls Is Nothing, 0, urls.Length)
                    Dim endIdx As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                    Dim slice As New List(Of String())
                    SyncLock fileLookup
                        For i As Integer = startingIndex To endIdx
                            Dim tags() As String = LoadFile(urls(i))
                            If tags IsNot Nothing Then slice.Add(tags)
                        Next
                    End SyncLock
                    emitted = WriteAudioFilesDIDL(writer, hostUrl, filterSet, "object.item.audioItem.musicTrack", "0", slice, 0, slice.Count)
                End If
                writer.WriteEndElement()
            End Using
            numberReturned = emitted.ToString()
            totalMatches = total.ToString()
            result = text.ToString()
            Return True
        End Function

        ' Extract the TERM in a "dc:title contains "TERM"" subexpression. Returns ""
        ' if the criteria doesn't include the pattern. Case-insensitive on the
        ' field name; preserves the term's original casing.
        Private Shared Function ExtractTitleContainsTerm(criteria As String) As String
            If String.IsNullOrEmpty(criteria) Then Return ""
            Return ExtractContainsTerm(criteria, "dc:title")
        End Function

        ' Extract the TERM in a `<field> contains "TERM"` subexpression, for any field.
        ' Case-insensitive on the field name; preserves the term's original casing.
        Private Shared Function ExtractContainsTerm(criteria As String, field As String) As String
            If String.IsNullOrEmpty(criteria) Then Return ""
            Dim marker As String = field & " contains """
            Dim idx As Integer = criteria.IndexOf(marker, StringComparison.OrdinalIgnoreCase)
            If idx < 0 Then Return ""
            idx += marker.Length
            Dim endIdx As Integer = criteria.IndexOf(""""c, idx)
            If endIdx < 0 Then Return ""
            Return criteria.Substring(idx, endIdx - idx)
        End Function

        ' Extract the artist term from a client's artist search. Clients send the two
        ' spellings interchangeably and BubbleUPnP sends BOTH OR'd together with the same
        ' term - `(dc:creator contains "X" or upnp:artist contains "X")` - so either hit
        ' yields the same string and one Artist condition covers the pair.
        Private Shared Function ExtractArtistContainsTerm(criteria As String) As String
            Dim term As String = ExtractContainsTerm(criteria, "upnp:artist")
            If term.Length > 0 Then Return term
            Return ExtractContainsTerm(criteria, "dc:creator")
        End Function

        ' True when the criteria carries ANY field predicate (a `contains` on any field).
        ' ⚠ CLAUDE: this is the guard that keeps HandleClassOnlySearch honest. That function
        ' answers "give me all albums / all tracks" by returning the WHOLE library, so it must
        ' only ever claim a query that really asks for everything. It used to decline solely on
        ' a dc:title term, so an artist search (no title term) was claimed, its predicate thrown
        ' away, and 329k tracks returned for a search that should match 579 - the client then
        ' paged through all of them. Any new searchable field must be handled in
        ' HandleLazySearch, never silently swallowed here.
        Private Shared Function HasFieldPredicate(criteria As String) As Boolean
            If String.IsNullOrEmpty(criteria) Then Return False
            Return criteria.IndexOf(" contains """, StringComparison.OrdinalIgnoreCase) >= 0
        End Function

        Public Sub Search(headers As Dictionary(Of String, String), containerId As String, searchCriteria As String, filter As String, startingIndex As Integer, requestedCount As Integer, sortCriteria As String, ByRef result As String, ByRef numberReturned As String, ByRef totalMatches As String)
            'Debug.WriteLine(containerId & ":" & searchCriteria & ",f=" & filter)
            If Settings.LogDebugInfo Then
                LogInformation("Search", "object=" & containerId & ",criteria=" & searchCriteria)
            End If
            LoadLibrary()
            If streamingProfile.WmcCompatability AndAlso (containerId = "F" OrElse searchCriteria = "upnp:class derivedfrom ""object.container.playlistContainer"" and @refID exists false") Then
                Browse(headers, "13", BrowseFlag.BrowseDirectChildren, filter, startingIndex, requestedCount, sortCriteria, result, numberReturned, totalMatches)
            ElseIf streamingProfile.WmcCompatability AndAlso (containerId = "5" OrElse containerId = "6" OrElse containerId = "7") Then
                LoadNode(template, tree, True)
                Browse(headers, If(containerId = "5", "1_103", If(containerId = "6", "1_101", "1_100")), BrowseFlag.BrowseDirectChildren, filter, startingIndex, requestedCount, sortCriteria, result, numberReturned, totalMatches)
            ElseIf HandleLazySearch(headers, containerId, searchCriteria, filter, startingIndex, requestedCount, result, numberReturned, totalMatches) Then
                ' Handled by the new lazy-aware search path (handles BubbleUPnP's
                ' "(upnp:class ... and dc:title contains "TERM")" queries for both
                ' track and album classes). Per-client scope substitution already
                ' applied upstream in ContentDirectoryService.ResolveSearchContainer.
            ElseIf HandleClassOnlySearch(headers, containerId, searchCriteria, filter, startingIndex, requestedCount, result, numberReturned, totalMatches) Then
                ' Handled: bare class query with no title term - BubbleUPnP's "Random Tracks"
                ' / "Random Albums", served (paged) from the lazy MB query path. Without this
                ' it fell through below to the empty `musicFiles` list and returned zero.
            Else
                Dim host As String
                Dim hostUrl As String = If(Not headers.TryGetValue("host", host), PrimaryHostUrl, "http://" & host)
                Dim text As New StringBuilder(16384)
                Dim filterSet As HashSet(Of String) = Nothing
                If filter <> "*" Then
                    filterSet = New HashSet(Of String)(filter.Split(New Char() {","c}, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
                End If
                Dim xmlSettings As New XmlWriterSettings With {
                    .OmitXmlDeclaration = True,
                    .Indent = False
                }
                Using writer As XmlWriter = XmlWriter.Create(text, xmlSettings)
                    writer.WriteStartElement("DIDL-Lite", "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/")
                    writer.WriteAttributeString("xmlns", "dc", Nothing, "http://purl.org/dc/elements/1.1/")
                    writer.WriteAttributeString("xmlns", "upnp", Nothing, "urn:schemas-upnp-org:metadata-1-0/upnp/")
                    'writer.WriteAttributeString("xmlns", "av", Nothing, "urn:schemas-sony-com:av")
                    writer.WriteAttributeString("xmlns", "pv", Nothing, "http://www.pv.com/pvns/")
                    Dim objectIds() As String = containerId.Split("_"c)
                    Dim matches As List(Of String()) = Nothing
                    If containerId = "0" Then
                        matches = musicFiles
                    Else
                        Dim node As TemplateNode
                        Dim folder As FolderNode
                        Dim lookupIdCount As Integer
                        If TryLocateNode(objectIds, node, folder, lookupIdCount) Then
                            matches = folder.ChildFiles
                        End If
                    End If
                    ' Album-class search: emit musicAlbum containers (one per distinct
                    ' album) instead of tracks. BubbleUPnP's "Random Albums" depends on this.
                    ' Restricted to root containerId - drilling into a sub-container with an
                    ' album-class query falls through to the normal track flow for now.
                    If matches IsNot Nothing AndAlso containerId = "0" AndAlso IsAlbumClassQuery(searchCriteria) Then
                        BuildSearchAlbumIndex()
                        Dim total As Integer = searchAlbumKeys.Count
                        Dim endIdx As Integer = startingIndex + requestedCount - 1
                        If endIdx >= total Then endIdx = total - 1
                        Dim written As Integer = 0
                        If startingIndex < total Then
                            For i As Integer = startingIndex To endIdx
                                Dim key As String = searchAlbumKeys(i)
                                Dim tracks As List(Of String()) = searchAlbumTracks(key)
                                Dim title As String = tracks(0)(MetaDataIndex.Album)
                                WriteContainerDIDL(writer, hostUrl, filterSet, "Salb" & i.ToString(), "0", tracks.Count.ToString(), title, "object.container.album.musicAlbum", tracks)
                                written += 1
                            Next
                        End If
                        numberReturned = written.ToString()
                        totalMatches = total.ToString()
                    ElseIf matches Is Nothing Then
                        totalMatches = "0"
                        numberReturned = "0"
                    Else
                        If searchCriteria.StartsWith("("c) Then
                            Dim charIndex As Integer = searchCriteria.IndexOf(")"c)
                            Dim scope As String = searchCriteria.Substring(1, charIndex - 1)
                            If String.Compare(scope, "upnp:class derivedfrom ""object.item.audioItem.musicTrack""", StringComparison.OrdinalIgnoreCase) = 0 OrElse
                               String.Compare(scope, "upnp:class derivedfrom ""object.item.audioItem""", StringComparison.OrdinalIgnoreCase) = 0 OrElse
                               String.Compare(scope, "upnp:class derivedfrom ""object.container.album.musicAlbum""", StringComparison.OrdinalIgnoreCase) = 0 Then
                                ' leave matches as-is
                            Else
                                matches = New List(Of String())
                            End If
                            searchCriteria = searchCriteria.Substring(charIndex + 1).TrimStart()
                            If searchCriteria.StartsWith("and ", StringComparison.OrdinalIgnoreCase) Then
                                searchCriteria = searchCriteria.Substring(4).TrimStart()
                                Dim field As String = searchCriteria.Substring(0, searchCriteria.IndexOf(" "c))
                                searchCriteria = searchCriteria.Substring(field.Length).TrimStart()
                                Dim criteria As String = searchCriteria.Substring(0, searchCriteria.IndexOf(" "c))
                                searchCriteria = searchCriteria.Substring(criteria.Length).TrimStart()
                                If (String.Compare(criteria, "contains", StringComparison.OrdinalIgnoreCase) = 0 OrElse criteria = "=") AndAlso searchCriteria.StartsWith(""""c) AndAlso searchCriteria.EndsWith(""""c) Then
                                    searchCriteria = searchCriteria.Substring(1, searchCriteria.Length - 2)
                                End If
                            End If
                            'JRiver: (upnp:class derivedfrom "object.item.audioItem.musicTrack")
                            '        (upnp:class derivedfrom "object.item.videoItem")
                            'upnp:genre, upnp:album
                            '(upnp:class derivedfrom "object.item.audioItem.musicTrack") and dc:title contains "<The Search Term you entered>"
                            '(upnp:class = ""object.container.album.musicAlbum"") and (upnp:artist = ""Amplifier"")
                            'upnp:class derivedfrom "object.item.audioItem" and @refID exists false
                        End If
                        numberReturned = WriteAudioFilesDIDL(writer, hostUrl, filterSet, "object.item.audioItem.musicTrack", "0", matches, startingIndex, requestedCount).ToString()
                        totalMatches = matches.Count.ToString()
                    End If
                End Using
                result = text.ToString()
            End If
        End Sub

        ' --- Lazy Browse handler ---
        ' Handles IDs prefixed with "L:". Designed to fetch data on demand from MB instead
        ' of relying on the eager LoadLibrary tree, so libraries with hundreds of thousands
        ' of tracks can render the UPnP tree without an upfront full scan.
        '
        ' Chunk 1 (current): stub only. Returns an empty DIDL response for any L: id.
        '   Nothing emits L: ids yet, so this code path is unreachable in production -
        '   the routing scaffolding is in place but inert.
        ' Chunk 2 (next):    first-level enumeration via Library_GetFileTag + filtered XML.
        ' Chunk 3+:          drill-down via Library_QueryLookupTable filtered queries.
        '
        ' ID grammar (forward design - subject to refinement during Chunk 2):
        '   L:<endpoint>:[<field>=<value>:]*[<field>]
        '     L:music                            → Music endpoint root (lazy variant)
        '     L:music:AlbumArtist                → list distinct AlbumArtists
        '     L:music:AlbumArtist=Beatles        → list albums for Beatles
        '     L:music:AlbumArtist=Beatles:Album=Abbey Road  → list tracks
        ' Values containing ':', '=', '\0' are URL-encoded with percent-escapes.
        ' Session cache of distinct grouping values for an endpoint+field pair.
        ' Key: "music:AlbumArtist" → sorted list of distinct values.
        ' Invalidated by ResetCache (which already runs on settings change) and would also
        ' be cleared on LibraryChanged in a future chunk.
        Private Shared ReadOnly lazyDistinctCache As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)

        ' Binding-driven Lazy Browse (Chunk 5).
        ' Walks the user's configured binding instead of hardcoding AlbumArtist→Album→Tracks.
        ' Currently supports the FIRST path of a multi-path binding only (multi-path = future).
        ' Leaf modes:
        '   T  - at depth = hierarchy.Length, emit tracks directly (no album layer)
        '   AT - at depth = hierarchy.Length, emit album containers; tracks at depth+1
        ' The album layer honours the path's AlbumGroupBy (composite identity + title + sort).
        Private Sub LazyBrowse(hostUrl As String, filterSet As HashSet(Of String), objectId As String, startingIndex As Integer, requestedCount As Integer, ByRef result As String, ByRef numberReturned As String, ByRef totalMatches As String)
            Dim text As New StringBuilder(16384)
            Dim xmlSettings As New XmlWriterSettings With {
                .OmitXmlDeclaration = True
            }
            Dim emitted As Integer = 0
            Dim total As Integer = 0
            Using writer As XmlWriter = XmlWriter.Create(text, xmlSettings)
                writer.WriteStartElement("DIDL-Lite", "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/")
                writer.WriteAttributeString("xmlns", "dc", Nothing, "http://purl.org/dc/elements/1.1/")
                writer.WriteAttributeString("xmlns", "upnp", Nothing, "urn:schemas-upnp-org:metadata-1-0/upnp/")
                writer.WriteAttributeString("xmlns", "pv", Nothing, "http://www.pv.com/pvns/")
                Dim endpoint As String = Nothing
                Dim pathIndex As Integer = -1
                Dim filters As List(Of LazyFilter) = Nothing
                If TryParseLazyId(objectId, endpoint, pathIndex, filters) Then
                    If String.Equals(endpoint, "filters", StringComparison.OrdinalIgnoreCase) Then
                        ' List of available filters (.xautopf files on disk). Wrapper level -
                        ' no library data fetched here, just a directory listing.
                        Dim entries As List(Of LazyResourceEntry) = EnumerateLazyFilters()
                        total = entries.Count
                        Dim endingIndex As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                        For i As Integer = startingIndex To endingIndex
                            Dim e As LazyResourceEntry = entries(i)
                            Dim childId As String = "L:filter:" & Uri.EscapeDataString(e.Id)
                            WriteLazyContainer(writer, childId, objectId, e.Name, "object.container", "")
                            emitted += 1
                        Next
                    ElseIf endpoint.StartsWith("filter:", StringComparison.OrdinalIgnoreCase) Then
                        ' Tracks for one filter - full binding-driven hierarchy, same as Music.
                        ' The filter's .xautopf conditions are wrapped INSIDE each per-level
                        ' smart-playlist query so MB narrows to the filter set before any
                        ' grouping. Per-leaf result is small (one album) which keeps
                        ' BubbleUPnP's per-track ffprobe pass bounded.
                        HandleLazyHierarchyEndpoint(writer, hostUrl, filterSet, objectId, endpoint, startingIndex, requestedCount, pathIndex, filters, emitted, total)
                    ElseIf String.Equals(endpoint, "playlists", StringComparison.OrdinalIgnoreCase) Then
                        ' MusicBee playlists may live inside folders (full name returned by
                        ' Playlist_GetName uses "\" as the separator, e.g. "Rock\Recent\Foo").
                        ' We split into a folder tree: the current folder path is encoded as
                        ' "f=<path>" in the id (empty/missing = root). Each browse renders
                        ' the immediate children - sub-folders + leaf playlists at that depth.
                        Dim currentFolder As String = ""
                        For Each f As LazyFilter In filters
                            If f IsNot Nothing AndAlso String.Equals(f.Field, "f", StringComparison.Ordinal) Then
                                currentFolder = f.Value
                                Exit For
                            End If
                        Next
                        Dim children As List(Of LazyPlaylistTreeNode) = BuildPlaylistFolderListing(currentFolder)
                        total = children.Count
                        Dim endingIndex As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                        For i As Integer = startingIndex To endingIndex
                            Dim node As LazyPlaylistTreeNode = children(i)
                            If node.IsFolder Then
                                ' Folder: drill stays in "playlists" endpoint with an updated f=.
                                Dim childId As String = "L:playlists:f=" & Uri.EscapeDataString(node.FolderPath)
                                WriteLazyContainer(writer, childId, objectId, node.Name, "object.container", "")
                            Else
                                ' Leaf playlist: jump to the playlist endpoint with its URL.
                                Dim childId As String = "L:playlist:" & Uri.EscapeDataString(node.PlaylistUrl)
                                WriteLazyContainer(writer, childId, objectId, node.Name, "object.container.playlistContainer", "")
                            End If
                            emitted += 1
                        Next
                    ElseIf endpoint.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) Then
                        ' Tracks for one playlist - Playlist_QueryFilesEx gives URLs; we cache
                        ' the URL list and LoadFile only the visible slice. Tracks render in
                        ' playlist order (NOT album order) so playback respects the curated
                        ' sequence.
                        Dim playlistUrl As String = endpoint.Substring("playlist:".Length)
                        Dim slice As List(Of String()) = GetLazyTracksSliceForPlaylist(playlistUrl, startingIndex, requestedCount, total)
                        For Each t As String() In slice
                            WriteAudioFileDIDL(writer, hostUrl, filterSet, objectId, t, "object.item.audioItem.musicTrack")
                            emitted += 1
                        Next
                    ElseIf String.Equals(endpoint, "music", StringComparison.OrdinalIgnoreCase) _
                        OrElse String.Equals(endpoint, "audiobook", StringComparison.OrdinalIgnoreCase) _
                        OrElse String.Equals(endpoint, "inbox", StringComparison.OrdinalIgnoreCase) _
                        OrElse String.Equals(endpoint, "radio", StringComparison.OrdinalIgnoreCase) _
                        OrElse String.Equals(endpoint, "podcast", StringComparison.OrdinalIgnoreCase) Then
                        ' All five share the binding-driven hierarchy walker. Differences
                        ' in data source (smart-playlist XML for music, in-memory list for
                        ' the other four) are absorbed by GetLazy*ForFilterIn → LazyEndpoint-
                        ' IsInMemory dispatch.
                        HandleLazyHierarchyEndpoint(writer, hostUrl, filterSet, objectId, endpoint, startingIndex, requestedCount, pathIndex, filters, emitted, total)
                    End If
                End If
                writer.WriteEndElement()
            End Using
            result = text.ToString()
            numberReturned = emitted.ToString()
            totalMatches = total.ToString()
            If Settings.LogDebugInfo Then
                LogInformation("LazyBrowse", "id=" & objectId & " emitted=" & emitted & " total=" & total)
            End If
        End Sub

        ' Shared binding-driven hierarchy walker. Music and per-filter endpoints both
        ' route here: their differences (broad domain query vs filter's xautopf as base
        ' condition) are absorbed by LazyBuildFilterQueryFor + GetLazy*ForFilterIn, which
        ' all dispatch on the endpoint string.
        ' One top-level child of a multi-path endpoint/filter, after first-field merging.
        ' Singleton (Members.Count = 1, empty SharedPrefix) → renders as a :p=<idx> path
        ' container. Merged (Members.Count > 1, non-empty SharedPrefix) → renders as a
        ' :_g=<entryIndex> group container: browsing it walks the shared prefix, then forks
        ' into one :p=<idx> child per member at the divergence point. Mirrors the eager
        ' BuildPartialMergedMultiPathRoot semantics (which is now dead/transitional code).
        Private NotInheritable Class LazyRootEntry
            Public Members As List(Of Integer)
            Public SharedPrefix As List(Of HierarchyEntry)
            Public ReadOnly Property IsMerged As Boolean
                Get
                    Return Members IsNot Nothing AndAlso Members.Count > 1
                End Get
            End Property
        End Class

        ' Group a binding's paths by their first hierarchy field (first-appearance order),
        ' then turn each group into root entries: a lone path is a singleton; a group of 2+
        ' that share a real prefix (ComputeSharedPrefix) merges; a group that can't merge
        ' (empty first field, or divergent/degenerate prefix) splits back into singletons.
        ' The returned list order IS the on-wire order, and an entry's index IS its :_g= id.
        Private Shared Function BuildLazyRootEntries(paths As BrowsePath()) As List(Of LazyRootEntry)
            Dim result As New List(Of LazyRootEntry)
            If paths Is Nothing Then Return result
            Dim groupKeys As New List(Of String)
            Dim groups As New Dictionary(Of String, List(Of Integer))(StringComparer.OrdinalIgnoreCase)
            For i As Integer = 0 To paths.Length - 1
                Dim k As String = ""
                Dim p As BrowsePath = paths(i)
                If p IsNot Nothing AndAlso p.Hierarchy IsNot Nothing AndAlso p.Hierarchy.Length > 0 AndAlso p.Hierarchy(0) IsNot Nothing Then
                    k = If(p.Hierarchy(0).Field, "")
                End If
                If Not groups.ContainsKey(k) Then
                    groups.Add(k, New List(Of Integer))
                    groupKeys.Add(k)
                End If
                groups(k).Add(i)
            Next
            For Each k As String In groupKeys
                Dim members As List(Of Integer) = groups(k)
                Dim sp As List(Of HierarchyEntry) = Nothing
                If members.Count > 1 AndAlso Not String.IsNullOrEmpty(k) Then
                    Dim memberPaths(members.Count - 1) As BrowsePath
                    For j As Integer = 0 To members.Count - 1
                        memberPaths(j) = paths(members(j))
                    Next
                    sp = ComputeSharedPrefix(memberPaths)
                End If
                If sp IsNot Nothing AndAlso sp.Count > 0 Then
                    result.Add(New LazyRootEntry With {.Members = members, .SharedPrefix = sp})
                Else
                    ' Can't merge - emit each member as its own singleton, preserving order.
                    For Each mi As Integer In members
                        result.Add(New LazyRootEntry With {.Members = New List(Of Integer) From {mi}, .SharedPrefix = New List(Of HierarchyEntry)})
                    Next
                End If
            Next
            Return result
        End Function

        ' Label for a merged group's divergence branch: the member path's short display
        ' with the shared prefix stripped off (so "Genre / Podcast People / …" shows as
        ' "Podcast People / …" under the already-chosen Genre).
        Private Shared Function StrippedPathShortDisplay(p As BrowsePath, prefixLen As Integer) As String
            Dim rest As New List(Of HierarchyEntry)
            If p.Hierarchy IsNot Nothing Then
                For j As Integer = prefixLen To p.Hierarchy.Length - 1
                    rest.Add(p.Hierarchy(j))
                Next
            End If
            Dim sp As New BrowsePath With {
                .Hierarchy = rest.ToArray(),
                .Leaf = p.Leaf,
                .IncludeAllTracks = p.IncludeAllTracks,
                .AlbumGroupBy = p.AlbumGroupBy
            }
            Return View.FormatBrowsePathShortDisplay(sp)
        End Function

        Private Sub HandleLazyHierarchyEndpoint(writer As XmlWriter, hostUrl As String, filterSet As HashSet(Of String), objectId As String, endpoint As String, startingIndex As Integer, requestedCount As Integer, pathIndex As Integer, filters As List(Of LazyFilter), ByRef emitted As Integer, ByRef total As Integer)
            Dim binding As EndpointBinding = View.GetBinding(endpoint)
            If binding Is Nothing OrElse binding.Paths Is Nothing OrElse binding.Paths.Length = 0 Then
                LogInformation("LazyQuery", "[HandleLazyHierarchy] endpoint=" & endpoint & " NO BINDING or empty paths - bindingNothing=" & (binding Is Nothing) & " pathsNothing=" & (binding IsNot Nothing AndAlso binding.Paths Is Nothing) & " pathsLen=" & If(binding IsNot Nothing AndAlso binding.Paths IsNot Nothing, binding.Paths.Length, -1))
                Return
            End If
            If LazyEndpointIsInMemory(endpoint) AndAlso filters.Count = 0 Then
                LogInformation("LazyQuery", "[HandleLazyHierarchy] endpoint=" & endpoint & " paths=" & binding.Paths.Length & " firstHierarchyLen=" & If(binding.Paths(0).Hierarchy IsNot Nothing, binding.Paths(0).Hierarchy.Length, -1))
            End If
            Dim multiPath As Boolean = (binding.Paths.Length > 1)
            ' The id prefix for the multi-path wrapper level. Mirrors the on-wire id
            ' format that TryParseLazyId reads: "L:<endpoint>[:p=N]…" where the resource
            ' portion of filter/playlist endpoints is URL-encoded.
            Dim idPrefix As String
            If endpoint.StartsWith("filter:", StringComparison.OrdinalIgnoreCase) Then
                idPrefix = "L:filter:" & Uri.EscapeDataString(endpoint.Substring("filter:".Length))
            ElseIf endpoint.StartsWith("playlist:", StringComparison.OrdinalIgnoreCase) Then
                idPrefix = "L:playlist:" & Uri.EscapeDataString(endpoint.Substring("playlist:".Length))
            Else
                idPrefix = "L:" & endpoint
            End If
            ' Merged-group marker: a :_g=<entryIndex> segment means we're walking the shared
            ' prefix of a first-field-merged group. Pull it out (and drop it from filters so
            ' it never reaches a query and doesn't shift the level depth); objectId keeps it,
            ' so descendant ids stay in group mode until the divergence fork.
            Dim groupIndex As Integer = -1
            For fi As Integer = filters.Count - 1 To 0 Step -1
                If filters(fi) IsNot Nothing AndAlso String.Equals(filters(fi).Field, "_g", StringComparison.Ordinal) Then
                    Integer.TryParse(filters(fi).Value, groupIndex)
                    filters.RemoveAt(fi)
                End If
            Next
            ' Hierarchical (slash-tree) prefix markers: a :_H_<field>=<prefix> segment tracks how
            ' deep we've drilled into a delimiter-split field. Pull them out (deepest per field =
            ' last in the id) and drop from filters so they don't shift level depth or reach a
            ' query - the SlashTree level reads the prefix from here; objectId keeps the markers.
            Dim slashPrefixes As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For fi As Integer = filters.Count - 1 To 0 Step -1
                Dim ff As LazyFilter = filters(fi)
                If ff IsNot Nothing AndAlso ff.Field IsNot Nothing AndAlso ff.Field.StartsWith("_H_", StringComparison.Ordinal) Then
                    Dim realF As String = ff.Field.Substring(3)
                    If Not slashPrefixes.ContainsKey(realF) Then slashPrefixes(realF) = If(ff.Value, "")
                    filters.RemoveAt(fi)
                End If
            Next
            If multiPath AndAlso filters.Count = 0 AndAlso pathIndex < 0 AndAlso groupIndex < 0 Then
                ' Root of a multi-path endpoint: list its merged root entries (singletons +
                ' first-field-merged groups), in BuildLazyRootEntries order.
                Dim entries As List(Of LazyRootEntry) = BuildLazyRootEntries(binding.Paths)
                ' Each root entry is labelled by its first field (consistent with merged groups:
                ' "Grouping", "Genre", …). A singleton falls back to its full short-path label
                ' only if another entry shares that first field (the rare non-merging split), so
                ' the two stay distinguishable.
                Dim firstFieldCount As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
                For Each en As LazyRootEntry In entries
                    Dim ffn As String = RootEntryFirstField(en, binding)
                    Dim cur As Integer = 0
                    firstFieldCount.TryGetValue(ffn, cur)
                    firstFieldCount(ffn) = cur + 1
                Next
                total = entries.Count
                Dim endingIndex As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                For ei As Integer = startingIndex To endingIndex
                    Dim entry As LazyRootEntry = entries(ei)
                    If entry.IsMerged Then
                        Dim label As String = View.FieldDisplayName(entry.SharedPrefix(0).Field)
                        WriteLazyContainer(writer, idPrefix & ":_g=" & ei, objectId, label, "object.container", "")
                    Else
                        Dim pi As Integer = entry.Members(0)
                        Dim ff As String = RootEntryFirstField(entry, binding)
                        Dim label As String
                        If ff.Length > 0 AndAlso firstFieldCount(ff) = 1 Then
                            label = View.FieldDisplayName(ff)
                        Else
                            label = View.FormatBrowsePathShortDisplay(binding.Paths(pi))
                        End If
                        WriteLazyContainer(writer, idPrefix & ":p=" & pi, objectId, label, "object.container", "")
                    End If
                    emitted += 1
                Next
                Return
            End If
            ' Resolve the path whose hierarchy we walk. In merged-group mode that's a synthetic
            ' path made of just the shared prefix (flat leaf → no album layer); the per-member
            ' divergence is emitted at the bottom instead of tracks.
            Dim mergedMembers As List(Of Integer) = Nothing
            Dim mergedPrefixLen As Integer = 0
            Dim path As BrowsePath
            If groupIndex >= 0 Then
                Dim entries As List(Of LazyRootEntry) = BuildLazyRootEntries(binding.Paths)
                If groupIndex >= entries.Count OrElse Not entries(groupIndex).IsMerged Then Return
                mergedMembers = entries(groupIndex).Members
                mergedPrefixLen = entries(groupIndex).SharedPrefix.Count
                path = New BrowsePath With {
                    .Hierarchy = entries(groupIndex).SharedPrefix.ToArray(),
                    .Leaf = LeafMode.T,
                    .IncludeAllTracks = False,
                    .AlbumGroupBy = Nothing
                }
            Else
                Dim effectivePathIndex As Integer = If(pathIndex < 0, 0, pathIndex)
                If effectivePathIndex < 0 OrElse effectivePathIndex >= binding.Paths.Length Then Return
                path = binding.Paths(effectivePathIndex)
            End If
            Dim levels As List(Of LazyLevel) = ExpandHierarchyLevels(path)
            Dim hasAllTracksMarker As Boolean = False
            For Each f As LazyFilter In filters
                If f IsNot Nothing AndAlso String.Equals(f.Field, "_AT", StringComparison.Ordinal) Then
                    hasAllTracksMarker = True
                    Exit For
                End If
            Next
            Dim depth As Integer = filters.Count
            ' Collapse single-result grouping levels. If the level at the current depth offers
            ' only one choice, don't make the control point show a pointless one-folder level -
            ' descend into it automatically by appending its filter + extending the container id,
            ' and repeat. Two collapsible level kinds:
            '   • ValueDistinct - exactly one distinct value (e.g. Record Type → "LP" for an
            '     artist that only has LPs). Letter-aware: when a letter bucket is active above,
            '     count only the values under that letter.
            '   • LetterBucket - the field's values fall under exactly one first-letter, so the
            '     A/B/C… level holds a single bucket; skip straight into it.
            ' Stops at the first level with a real choice (>1), the album layer (a real
            ' destination), or the leaf. [All Tracks] is NOT a reason to skip collapsing: it
            ' reappears at every level (IncludeAllTracks), so it's still reachable one level down.
            ' Only skipped when the user has actually entered [All Tracks] (hasAllTracksMarker).
            If Not hasAllTracksMarker Then
                Do While depth < levels.Count
                    Dim lvl As LazyLevel = levels(depth)
                    If lvl.Kind = LazyLevelKind.LetterBucket Then
                        Dim allVals As List(Of String) = GetLazyDistinctForFilterIn(endpoint, filters, LazyFieldNameToFetchTag(lvl.Field), lvl.Field)
                        Dim usedLetters As New SortedSet(Of Char)()
                        For Each v As String In allVals
                            usedLetters.Add(LazyBucketChar(v, lvl.Field))
                        Next
                        If usedLetters.Count <> 1 Then Exit Do
                        Dim letterList As New List(Of Char)(usedLetters)
                        Dim onlyLetter As String = letterList(0).ToString()
                        filters.Add(New LazyFilter With {.Field = "_L_" & lvl.Field, .Value = onlyLetter})
                        objectId = objectId & ":_L_" & lvl.Field & "=" & Uri.EscapeDataString(onlyLetter)
                        depth += 1
                    ElseIf lvl.Kind = LazyLevelKind.ValueDistinct Then
                        Dim vals As List(Of String) = GetLazyDistinctForFilterIn(endpoint, filters, LazyFieldNameToFetchTag(lvl.Field), lvl.Field)
                        Dim activeLetter As String = ActiveLetterAt(levels, filters, depth)
                        If activeLetter.Length > 0 Then
                            Dim wanted As Char = Char.ToUpperInvariant(activeLetter(0))
                            Dim filtered As New List(Of String)
                            For Each v As String In vals
                                If LazyBucketChar(v, lvl.Field) = wanted Then filtered.Add(v)
                            Next
                            vals = filtered
                        End If
                        If vals.Count <> 1 Then Exit Do
                        filters.Add(New LazyFilter With {.Field = lvl.Field, .Value = vals(0)})
                        objectId = objectId & ":" & lvl.Field & "=" & Uri.EscapeDataString(vals(0))
                        depth += 1
                    ElseIf lvl.Kind = LazyLevelKind.SlashTree Then
                        ' Collapse a single-choice slash level: one branch with no [exact] sibling
                        ' → drill into it automatically; one leaf → commit it and advance a level.
                        Dim sChar As String = Plugin.Settings.HierarchicalCharFor(lvl.Field)
                        Dim sPrefix As String = Nothing
                        If Not slashPrefixes.TryGetValue(lvl.Field, sPrefix) Then sPrefix = ""
                        Dim sInfo As SlashLevelInfo = ComputeSlashLevel(endpoint, filters, lvl.Field, sChar, sPrefix)
                        If sInfo.Segments.Count <> 1 OrElse sInfo.ExactPrefixExists Then Exit Do
                        Dim onlySeg As SlashSeg = sInfo.Segments(0)
                        If onlySeg.IsBranch Then
                            Dim newP As String = If(sPrefix.Length > 0, sPrefix & sChar, "") & onlySeg.Name
                            slashPrefixes(lvl.Field) = newP
                            objectId = objectId & ":_H_" & lvl.Field & "=" & Uri.EscapeDataString(newP)
                            ' stay on this slash level (depth unchanged) - loop re-evaluates deeper
                        Else
                            filters.Add(New LazyFilter With {.Field = lvl.Field, .Value = onlySeg.LeafValue})
                            objectId = objectId & ":" & lvl.Field & "=" & Uri.EscapeDataString(onlySeg.LeafValue)
                            depth += 1
                        End If
                    Else
                        Exit Do  ' AlbumLayer or other - a real destination, never collapsed.
                    End If
                Loop
            End If
            If mergedMembers IsNot Nothing AndAlso depth >= levels.Count Then
                ' Divergence point: the shared prefix is fully chosen. Fork into one container
                ' per member path, swapping the :_g=<idx> token for that member's :p=<idx>
                ' while carrying the accumulated shared-prefix filters (the objectId suffix).
                ' From there the normal per-path walker takes over at the right depth.
                Dim groupNodeId As String = idPrefix & ":_g=" & groupIndex
                Dim suffix As String = If(objectId.Length >= groupNodeId.Length, objectId.Substring(groupNodeId.Length), "")
                total = mergedMembers.Count
                Dim endingIndexD As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                For i As Integer = startingIndex To endingIndexD
                    Dim memberIdx As Integer = mergedMembers(i)
                    Dim childId As String = idPrefix & ":p=" & memberIdx & suffix
                    WriteLazyContainer(writer, childId, objectId, StrippedPathShortDisplay(binding.Paths(memberIdx), mergedPrefixLen), "object.container", "")
                    emitted += 1
                Next
                Return
            End If
            If hasAllTracksMarker OrElse depth >= levels.Count Then
                Dim tracks As List(Of String()) = GetLazyTracksForFilterIn(endpoint, filters)
                total = tracks.Count
                Dim endingIndex As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                For i As Integer = startingIndex To endingIndex
                    WriteAudioFileDIDL(writer, hostUrl, filterSet, objectId, tracks(i), "object.item.audioItem.musicTrack")
                    emitted += 1
                Next
                Return
            End If
            Dim currentLevel As LazyLevel = levels(depth)
            Dim includeAllTracks As Boolean = path.IncludeAllTracks
            Select Case currentLevel.Kind
                Case LazyLevelKind.LetterBucket
                    Dim nextMdt As Plugin.MetaDataType = LazyFieldNameToFetchTag(currentLevel.Field)
                    Dim allValues As List(Of String) = GetLazyDistinctForFilterIn(endpoint, filters, nextMdt, currentLevel.Field)
                    Dim usedLetters As New SortedSet(Of Char)()
                    For Each v As String In allValues
                        usedLetters.Add(LazyBucketChar(v, currentLevel.Field))
                    Next
                    Dim letterList As New List(Of Char)(usedLetters)
                    If currentLevel.Entry IsNot Nothing AndAlso currentLevel.Entry.SortDescending Then letterList.Reverse()
                    Dim items As New List(Of LazyEmittable)
                    If includeAllTracks Then items.Add(LazyEmittable.AllTracksMarker())
                    For Each c As Char In letterList
                        items.Add(LazyEmittable.Container(c.ToString(), "object.container", ":_L_" & currentLevel.Field & "=" & Uri.EscapeDataString(c.ToString())))
                    Next
                    total = items.Count
                    Dim endingIndex As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                    For i As Integer = startingIndex To endingIndex
                        EmitLazyItem(writer, objectId, items(i), hostUrl, filterSet)
                        emitted += 1
                    Next
                Case LazyLevelKind.ValueDistinct
                    Dim nextField As String = currentLevel.Field
                    Dim nextMdt As Plugin.MetaDataType = LazyFieldNameToFetchTag(nextField)
                    Dim allValues As List(Of String) = GetLazyDistinctForFilterIn(endpoint, filters, nextMdt, nextField)
                    Dim activeLetter As String = ActiveLetterAt(levels, filters, depth)
                    Dim values As List(Of String)
                    If activeLetter.Length > 0 Then
                        values = New List(Of String)
                        Dim wantedChar As Char = Char.ToUpperInvariant(activeLetter(0))
                        For Each v As String In allValues
                            If LazyBucketChar(v, nextField) = wantedChar Then values.Add(v)
                        Next
                    Else
                        values = New List(Of String)(allValues)
                    End If
                    If currentLevel.Entry IsNot Nothing AndAlso currentLevel.Entry.SortDescending Then values.Reverse()
                    Dim containerClass As String = LazyContainerClassForField(nextField)
                    Dim items As New List(Of LazyEmittable)
                    If includeAllTracks Then items.Add(LazyEmittable.AllTracksMarker())
                    For Each v As String In values
                        items.Add(LazyEmittable.Container(v, containerClass, ":" & nextField & "=" & Uri.EscapeDataString(v)))
                    Next
                    total = items.Count
                    Dim endingIndex As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                    For i As Integer = startingIndex To endingIndex
                        EmitLazyItem(writer, objectId, items(i), hostUrl, filterSet)
                        emitted += 1
                    Next
                Case LazyLevelKind.AlbumLayer
                    Dim albumGroupBy() As Plugin.AlbumGroupField = NormalizeAlbumGroupBy(path.AlbumGroupBy)
                    Dim entriesUnsorted As List(Of LazyAlbumEntry) = GetLazyAlbumsForFilterIn(endpoint, filters, albumGroupBy)
                    Dim entries As List(Of LazyAlbumEntry) = SortLazyAlbums(entriesUnsorted, albumGroupBy)
                    Dim items As New List(Of LazyEmittable)
                    If includeAllTracks Then items.Add(LazyEmittable.AllTracksMarker())
                    For Each e As LazyAlbumEntry In entries
                        items.Add(LazyEmittable.AlbumContainer(e))
                    Next
                    total = items.Count
                    Dim endingIndex As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                    For i As Integer = startingIndex To endingIndex
                        EmitLazyItem(writer, objectId, items(i), hostUrl, filterSet)
                        emitted += 1
                    Next
                Case LazyLevelKind.SlashTree
                    ' Render a delimiter-split field as a drill-down tree. `prefix` is the path
                    ' chosen so far (empty at the field's top level); we list the next segment
                    ' under it. A segment with deeper values → a branch (drill via :_H_); a
                    ' segment that is a full value → a leaf (commits :field=value, advancing to
                    ' the next hierarchy level). If a value equals the prefix exactly AND deeper
                    ' values exist, a bracketed [prefix] leaf is added for the "tagged exactly
                    ' here" tracks. Multi-value tags are split on ";" first, then the delimiter.
                    Dim treeField As String = currentLevel.Field
                    Dim treeChar As String = Plugin.Settings.HierarchicalCharFor(treeField)
                    Dim prefix As String = Nothing
                    If Not slashPrefixes.TryGetValue(treeField, prefix) Then prefix = ""
                    Dim prefixPlus As String = If(prefix.Length > 0, prefix & treeChar, "")
                    Dim info As SlashLevelInfo = ComputeSlashLevel(endpoint, filters, treeField, treeChar, prefix)
                    info.Segments.Sort(Function(a, b) String.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase))
                    If currentLevel.Entry IsNot Nothing AndAlso currentLevel.Entry.SortDescending Then info.Segments.Reverse()
                    Dim leafClass As String = LazyContainerClassForField(treeField)
                    Dim items As New List(Of LazyEmittable)
                    If info.ExactPrefixExists AndAlso info.Segments.Count > 0 Then
                        Dim pSeg As String = prefix
                        Dim pci As Integer = prefix.LastIndexOf(treeChar, StringComparison.Ordinal)
                        If pci >= 0 Then pSeg = prefix.Substring(pci + 1)
                        items.Add(LazyEmittable.Container("[" & pSeg & "]", leafClass, ":" & treeField & "=" & Uri.EscapeDataString(prefix)))
                    End If
                    For Each s As SlashSeg In info.Segments
                        If s.IsBranch Then
                            items.Add(LazyEmittable.Container(s.Name, "object.container", ":_H_" & treeField & "=" & Uri.EscapeDataString(prefixPlus & s.Name)))
                        Else
                            items.Add(LazyEmittable.Container(s.Name, leafClass, ":" & treeField & "=" & Uri.EscapeDataString(s.LeafValue)))
                        End If
                    Next
                    total = items.Count
                    Dim endingIndexS As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                    For i As Integer = startingIndex To endingIndexS
                        EmitLazyItem(writer, objectId, items(i), hostUrl, filterSet)
                        emitted += 1
                    Next
            End Select
        End Sub

        ' (field, decoded-value) pair recovered from a lazy object id.
        ' Letter-bucket filters use field names of the form "_L_<RealField>" and ARE NOT
        ' included in smart-playlist queries - they're client-side prefix filters applied
        ' to the distinct-value list of the next level.
        Private NotInheritable Class LazyFilter
            Public Field As String
            Public Value As String
        End Class

        ' Expanded drill levels for one BrowsePath. Letter-bucketed hierarchy entries
        ' contribute TWO levels (Letter, then Value); plain entries contribute one.
        ' AT leaf adds a trailing Album level. Tracks are the implicit leaf past the last.
        Private Enum LazyLevelKind
            LetterBucket
            ValueDistinct
            AlbumLayer
            SlashTree
        End Enum
        Private NotInheritable Class LazyLevel
            Public Kind As LazyLevelKind
            Public Field As String           ' for LetterBucket / ValueDistinct
            Public Entry As HierarchyEntry   ' source HierarchyEntry (for SortDescending etc.)
        End Class

        Private Shared Function ExpandHierarchyLevels(path As BrowsePath) As List(Of LazyLevel)
            Dim levels As New List(Of LazyLevel)
            If path Is Nothing Then Return levels
            If path.Hierarchy IsNot Nothing Then
                For Each entry As HierarchyEntry In path.Hierarchy
                    If entry Is Nothing OrElse String.IsNullOrEmpty(entry.Field) Then Continue For
                    If Plugin.Settings.HierarchicalCharFor(entry.Field).Length > 0 Then
                        ' Field configured as hierarchical → one slash-tree level (its values are
                        ' split on the delimiter and drilled prefix-by-prefix). Letter-bucketing
                        ' is ignored for such fields - the two don't combine sensibly.
                        levels.Add(New LazyLevel With {.Kind = LazyLevelKind.SlashTree, .Field = entry.Field, .Entry = entry})
                    Else
                        If entry.BucketByLetter Then
                            levels.Add(New LazyLevel With {.Kind = LazyLevelKind.LetterBucket, .Field = entry.Field, .Entry = entry})
                        End If
                        levels.Add(New LazyLevel With {.Kind = LazyLevelKind.ValueDistinct, .Field = entry.Field, .Entry = entry})
                    End If
                Next
            End If
            If path.Leaf = LeafMode.AT Then
                levels.Add(New LazyLevel With {.Kind = LazyLevelKind.AlbumLayer})
            End If
            Return levels
        End Function

        ' Drop pseudo-filters that aren't real MB tag conditions:
        '   _L_<Field>=<letter>  → letter-bucket marker, client-side prefix filter only
        '   _AT=1                → "[All Tracks]" marker, signals "skip remaining levels"
        Private Shared Function RealFiltersOnly(filters As List(Of LazyFilter)) As List(Of LazyFilter)
            Dim result As New List(Of LazyFilter)
            For Each f As LazyFilter In filters
                If f Is Nothing OrElse String.IsNullOrEmpty(f.Field) Then Continue For
                If f.Field.StartsWith("_L_", StringComparison.Ordinal) Then Continue For
                If f.Field.StartsWith("_H_", StringComparison.Ordinal) Then Continue For
                If String.Equals(f.Field, "_AT", StringComparison.Ordinal) Then Continue For
                result.Add(f)
            Next
            Return result
        End Function

        ' If the previous level was a LetterBucket for the field we're about to render,
        ' return that letter so the caller can prefix-filter the distinct-value list.
        ' Returns "" when no letter is in effect for this depth.
        ' The first hierarchy field of a root entry (merged → shared prefix's first field;
        ' singleton → its path's first hierarchy field). "" for an empty-hierarchy path.
        Private Shared Function RootEntryFirstField(entry As LazyRootEntry, binding As EndpointBinding) As String
            If entry Is Nothing Then Return ""
            If entry.IsMerged AndAlso entry.SharedPrefix IsNot Nothing AndAlso entry.SharedPrefix.Count > 0 AndAlso entry.SharedPrefix(0) IsNot Nothing Then
                Return If(entry.SharedPrefix(0).Field, "")
            End If
            If entry.Members IsNot Nothing AndAlso entry.Members.Count > 0 Then
                Dim p As BrowsePath = binding.Paths(entry.Members(0))
                If p IsNot Nothing AndAlso p.Hierarchy IsNot Nothing AndAlso p.Hierarchy.Length > 0 AndAlso p.Hierarchy(0) IsNot Nothing Then Return If(p.Hierarchy(0).Field, "")
            End If
            Return ""
        End Function

        Private NotInheritable Class SlashSeg
            Public Name As String
            Public IsBranch As Boolean    ' True = has deeper values (drill via :_H_); False = a full value (leaf)
            Public LeafValue As String    ' the full value, for leaf segments
        End Class

        Private NotInheritable Class SlashLevelInfo
            Public ExactPrefixExists As Boolean   ' a value equals the prefix exactly (→ [prefix] node)
            Public Segments As New List(Of SlashSeg)
        End Class

        ' One level of a delimiter-split (hierarchical) field. Given the accumulated prefix,
        ' returns whether a value equals it exactly plus the distinct next segments under it
        ' (each flagged branch = has deeper values, or leaf = a complete value). Multi-value
        ' tags are split on ";" first, then the tree character. Shared by the slash-level
        ' collapse and emission so the two never drift.
        Private Shared Function ComputeSlashLevel(endpoint As String, filters As List(Of LazyFilter), field As String, treeChar As String, prefix As String) As SlashLevelInfo
            Dim info As New SlashLevelInfo()
            If String.IsNullOrEmpty(treeChar) Then Return info
            Dim mdt As Plugin.MetaDataType = LazyFieldNameToFetchTag(field)
            Dim rawValues As List(Of String) = GetLazyDistinctForFilterIn(endpoint, filters, mdt, field)
            Dim prefixPlus As String = If(prefix.Length > 0, prefix & treeChar, "")
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim segIndex As New Dictionary(Of String, SlashSeg)(StringComparer.OrdinalIgnoreCase)
            For Each raw As String In rawValues
                If raw Is Nothing Then Continue For
                For Each part As String In raw.Split(";"c)
                    Dim v As String = part.Trim()
                    If v.Length = 0 OrElse Not seen.Add(v) Then Continue For
                    If prefix.Length > 0 AndAlso String.Equals(v, prefix, StringComparison.OrdinalIgnoreCase) Then
                        info.ExactPrefixExists = True
                        Continue For
                    End If
                    If prefix.Length > 0 AndAlso Not v.StartsWith(prefixPlus, StringComparison.OrdinalIgnoreCase) Then Continue For
                    Dim rest As String = If(prefix.Length > 0, v.Substring(prefixPlus.Length), v)
                    If rest.Length = 0 Then Continue For
                    Dim ci As Integer = rest.IndexOf(treeChar, StringComparison.Ordinal)
                    Dim seg As String = If(ci >= 0, rest.Substring(0, ci), rest)
                    If seg.Length = 0 Then Continue For
                    Dim s As SlashSeg = Nothing
                    If Not segIndex.TryGetValue(seg, s) Then
                        s = New SlashSeg With {.Name = seg, .IsBranch = False, .LeafValue = prefixPlus & seg}
                        segIndex(seg) = s
                        info.Segments.Add(s)
                    End If
                    If ci >= 0 Then s.IsBranch = True
                Next
            Next
            Return info
        End Function

        Private Shared Function ActiveLetterAt(levels As List(Of LazyLevel), filters As List(Of LazyFilter), depth As Integer) As String
            If depth <= 0 OrElse depth - 1 >= levels.Count OrElse depth - 1 >= filters.Count Then Return ""
            If levels(depth - 1).Kind <> LazyLevelKind.LetterBucket Then Return ""
            Return filters(depth - 1).Value
        End Function

        ' Single emittable item - either an "[All Tracks]" marker (which produces a
        ' container ending in :_AT=1), a regular grouping container with explicit id
        ' suffix, or an album container with artwork. Pulls the three emit cases out
        ' of the LazyBrowse switch so each level can choose what to emit by appending
        ' to a single list (e.g. prepending an [All Tracks] entry for IncludeAllTracks).
        Private NotInheritable Class LazyEmittable
            Public Kind As Integer                ' 0=AllTracks, 1=Container, 2=AlbumContainer
            Public Title As String
            Public ContainerClass As String
            Public IdSuffix As String             ' for Kind=Container: id = parent & IdSuffix
            Public Album As LazyAlbumEntry        ' for Kind=AlbumContainer
            Public Shared Function AllTracksMarker() As LazyEmittable
                Return New LazyEmittable With {.Kind = 0, .Title = "[All Tracks]", .ContainerClass = "object.container", .IdSuffix = ":_AT=1"}
            End Function
            Public Shared Function Container(title As String, containerClass As String, idSuffix As String) As LazyEmittable
                Return New LazyEmittable With {.Kind = 1, .Title = title, .ContainerClass = containerClass, .IdSuffix = idSuffix}
            End Function
            Public Shared Function AlbumContainer(album As LazyAlbumEntry) As LazyEmittable
                Return New LazyEmittable With {.Kind = 2, .Album = album}
            End Function
        End Class

        Private Sub EmitLazyItem(writer As XmlWriter, parentId As String, item As LazyEmittable, hostUrl As String, filterSet As HashSet(Of String))
            If item.Kind = 2 Then
                Dim e As LazyAlbumEntry = item.Album
                ' Composite album identity: append one :field=value per album group-by field so
                ' drilling into the album re-queries exactly its tracks (Field Is value, AND-ed).
                Dim sb As New StringBuilder(parentId)
                For Each kf As LazyFilter In e.Key
                    sb.Append(":"c).Append(kf.Field).Append("="c).Append(Uri.EscapeDataString(kf.Value))
                Next
                WriteLazyContainer(writer, sb.ToString(), parentId, e.Title, "object.container.album.musicAlbum", "", hostUrl, filterSet, e.TrackUrl)
            Else
                ' Pass hostUrl + filterSet so musicAlbum containers emitted at the
                ' ValueDistinct level (e.g. podcast subscriptions when the user's
                ' binding groups by Album instead of using an AT leaf) can still
                ' resolve artwork. Without these, canEmitArt was false and no
                ' <upnp:albumArtURI> ever got emitted.
                WriteLazyContainer(writer, parentId & item.IdSuffix, parentId, item.Title, item.ContainerClass, "", hostUrl, filterSet)
            End If
        End Sub

        ' First-letter bucket. Name-style for person fields (handles "The Beatles" → B
        ' via ignoreNamePrefixes); plain first-char otherwise.
        Private Shared Function LazyBucketChar(value As String, fieldName As String) As Char
            If String.Equals(fieldName, "Genre", StringComparison.OrdinalIgnoreCase) Then
                Return GetBucket(value)
            End If
            Return GetNameBucket(value)
        End Function

        ' Parse "L:<endpoint>[:p=<N>][:<field>=<value>]*" into endpoint + path index +
        ' ordered filter list. Encoded values (Uri.EscapeDataString) round-trip cleanly
        ' because the encoding escapes ':' and '=' to %3A / %3D, leaving the bare
        ' characters as structural delimiters. pathIndex = -1 means "not specified" -
        ' callers default to 0 for single-path bindings or to "emit wrappers" for
        ' multi-path bindings at the endpoint root.
        Private Shared Function TryParseLazyId(objectId As String, ByRef endpoint As String, ByRef pathIndex As Integer, ByRef filters As List(Of LazyFilter)) As Boolean
            endpoint = Nothing
            pathIndex = -1
            filters = New List(Of LazyFilter)
            If objectId Is Nothing OrElse Not objectId.StartsWith("L:", StringComparison.Ordinal) Then Return False
            Dim parts() As String = objectId.Substring(2).Split(":"c)
            If parts.Length = 0 Then Return False
            endpoint = parts(0)
            Dim startIdx As Integer = 1
            ' Resource endpoints (one specific filter / playlist) carry their identifier as
            ' a second bare (no "=") segment. Merge it into the endpoint so dispatch can
            ' see "filter:<name>" / "playlist:<url>" as a single key.
            If parts.Length > 1 AndAlso _
               (String.Equals(endpoint, "filter", StringComparison.OrdinalIgnoreCase) OrElse _
                String.Equals(endpoint, "playlist", StringComparison.OrdinalIgnoreCase)) AndAlso _
               parts(1).IndexOf("="c) < 0 Then
                endpoint = endpoint & ":" & Uri.UnescapeDataString(parts(1))
                startIdx = 2
            End If
            If parts.Length > startIdx AndAlso parts(startIdx).StartsWith("p=", StringComparison.Ordinal) Then
                Dim n As Integer = 0
                Integer.TryParse(parts(startIdx).Substring(2), n)
                pathIndex = n
                startIdx += 1
            End If
            For i As Integer = startIdx To parts.Length - 1
                Dim part As String = parts(i)
                Dim eqIdx As Integer = part.IndexOf("="c)
                If eqIdx < 0 Then Continue For  ' bare field name from a stale Chunk-2 id; ignore
                Dim k As String = part.Substring(0, eqIdx)
                Dim v As String = Uri.UnescapeDataString(part.Substring(eqIdx + 1))
                filters.Add(New LazyFilter With {.Field = k, .Value = v})
            Next
            Return True
        End Function

        ' Build the MB query for QueryFilesEx from the accumulated filters.
        '   No filters → multi-domain broad query (note: single-domain "domain=Music"
        '                is rejected by this MB version; see LazyEndpointDomainQuery).
        '   ≥1 filter  → smart-playlist XML with one Condition per filter, ALL combined.
        ' Build a QueryFilesEx query for an endpoint at a given filter depth. Three shapes:
        '   "music" + no level filters → multi-domain broad query string
        '   "music" + level filters     → smart-playlist XML with the level conditions
        '   "filter:<basename>" + any   → smart-playlist XML wrapping the filter's own
        '                                 .xautopf conditions PLUS the level conditions,
        '                                 combined with CombineMethod="All"
        ' Lets Music and Filter share the same hierarchy walker.
        Private Shared Function LazyBuildFilterQuery(filters As List(Of LazyFilter)) As String
            Return LazyBuildFilterQueryFor("music", filters)
        End Function

        Private Shared Function LazyBuildFilterQueryFor(endpoint As String, filters As List(Of LazyFilter)) As String
            Dim real As List(Of LazyFilter) = RealFiltersOnly(filters)
            Dim baseConditions As String = ""
            If endpoint IsNot Nothing AndAlso endpoint.StartsWith("filter:", StringComparison.OrdinalIgnoreCase) Then
                baseConditions = GetFilterBaseConditions(endpoint.Substring("filter:".Length))
            End If
            ' If we have neither base conditions nor level filters, fall back to the
            ' broad domain query (only valid for music - filter without conditions is
            ' degenerate and would have nothing to match).
            If real.Count = 0 AndAlso baseConditions.Length = 0 Then
                Return LazyEndpointDomainQuery("music")
            End If
            Dim conds As New StringBuilder
            conds.Append("<Conditions CombineMethod=""All"">")
            If baseConditions.Length > 0 Then conds.Append(baseConditions)
            For Each f As LazyFilter In real
                If LazyFieldNeedsBoundaryAwareCompare(f.Field) Then
                    conds.Append(BuildMultiValueAwareCondition(f.Field, f.Value))
                Else
                    conds.Append("<Condition Field=""").Append(f.Field).Append(""" Comparison=""Is"" Value=""").Append(XmlAttributeEscape(f.Value)).Append(""" />")
                End If
            Next
            conds.Append("</Conditions>")
            Return "<SmartPlaylist><Source Type=""1"">" & conds.ToString() & "</Source></SmartPlaylist>"
        End Function

        ' Fields where MB's Comparison="Is" doesn't match multi-value tags correctly.
        ' Empirically discovered 2026-05; the common factor is that all Sort* variants
        ' fail while their non-Sort counterparts (AlbumArtist, Artist, Album, …) work
        ' correctly on structurally identical multi-value data. Probably one MB
        ' comparator dispatch path; expect the full set of Sort* fields to surface here
        ' as users encounter them. Lookup is case-insensitive.
        '
        '   SortArtist
        '   SortAlbumArtist
        '   SortAlbum
        '   SortComposer
        '   SortTitle
        '
        ' For any field in this list, drill conditions are emitted as a boundary-aware
        ' 4-way OR (see BuildMultiValueAwareCondition). For everything else we use the
        ' simple `Is` form - smaller XML, faster MB query, and avoids over-matching
        ' caveats that the boundary-aware form might introduce in edge cases.
        Private Shared ReadOnly LazyMultiValueBuggyFields() As String = New String() {
            "SortArtist",
            "SortAlbumArtist",
            "SortAlbum",
            "SortComposer",
            "SortTitle"
        }

        Private Shared Function LazyFieldNeedsBoundaryAwareCompare(field As String) As Boolean
            If String.IsNullOrEmpty(field) Then Return False
            For Each f As String In LazyMultiValueBuggyFields
                If String.Equals(f, field, StringComparison.OrdinalIgnoreCase) Then Return True
            Next
            Return False
        End Function

        ' Build a drill condition that matches the value against a (possibly multi-value)
        ' tag. Why this is needed:
        '   • MB's tag API serializes multi-value tags as "; "-joined strings (e.g. a track
        '     with two ALBUMARTISTSORT entries returns "yaiol; Ars Ricercata").
        '   • Our client-side distinct-value builder splits on "; " so the user sees both
        '     "yaiol" and "Ars Ricercata" as separate clickable folders.
        '   • MB's smart-playlist Comparison="Is" compares against the *whole* serialized
        '     string, which means `Is "Ars Ricercata"` does NOT match a track whose tag
        '     is "yaiol; Ars Ricercata". MB is also inconsistent here: `Is` works for some
        '     fields (AlbumArtist) and fails for others (SortAlbumArtist) on the same data.
        ' Workaround: emit a 4-way OR that covers all positional variants of the value
        ' within the multi-value string. No false positives because we anchor each variant
        ' with "; " delimiters.
        '   <Condition Field=F Is V>                 ← single-value tag (V is the whole tag)
        '     <Or Any>
        '       <Condition Field=F StartsWith "V; "> ← multi-value, V is first
        '       <Condition Field=F EndsWith "; V">   ← multi-value, V is last
        '       <Condition Field=F Contains "; V; "> ← multi-value, V is in the middle
        '     </Or>
        '   </Condition>
        ' MB accepts flat <Or> with multiple <Condition> children (verified via Debug tab).
        Private Shared Function BuildMultiValueAwareCondition(field As String, value As String) As String
            Dim esc As String = XmlAttributeEscape(value)
            Dim sb As New StringBuilder(256)
            sb.Append("<Condition Field=""").Append(field).Append(""" Comparison=""Is"" Value=""").Append(esc).Append(""">")
            sb.Append("<Or CombineMethod=""Any"">")
            sb.Append("<Condition Field=""").Append(field).Append(""" Comparison=""StartsWith"" Value=""").Append(esc).Append("; "" />")
            sb.Append("<Condition Field=""").Append(field).Append(""" Comparison=""EndsWith"" Value=""; ").Append(esc).Append(""" />")
            sb.Append("<Condition Field=""").Append(field).Append(""" Comparison=""Contains"" Value=""; ").Append(esc).Append("; "" />")
            sb.Append("</Or>")
            sb.Append("</Condition>")
            Return sb.ToString()
        End Function

        ' Stable cache key for the REAL filter slice. Letter filters don't affect the
        ' fetched distinct value set, so they're excluded - that way the cache hit on
        ' "all artists" survives across different active letter buckets.
        Private Shared Function LazyFiltersToCacheKey(filters As List(Of LazyFilter)) As String
            Dim real As List(Of LazyFilter) = RealFiltersOnly(filters)
            If real.Count = 0 Then Return ""
            Dim sb As New StringBuilder
            For Each f As LazyFilter In real
                If sb.Length > 0 Then sb.Append("|")
                sb.Append(f.Field).Append("=").Append(f.Value)
            Next
            Return sb.ToString()
        End Function

        ' Field-name → MB API tag code for the lazy single-tag fetch path. Pure generic
        ' name lookup - the token IS the field, no per-field aliasing. Parses against
        ' ItemManager's MetaDataType, which is a SUPERSET of Plugin.MetaDataType: it also
        ' carries codes the MB-API enum omits but Library_GetFileTag still accepts - notably
        ' YearOnly = 35 (the bare 4-digit year, distinct from Year = 88 = the full date tag).
        ' Because View.FieldNameToMetaDataType parses only the MB-API enum, it can't resolve
        ' those superset-only names; this lookup can. Falls back to View for synthetic names
        ' (e.g. MusicBeeFolder) that live in neither MetaDataType enum.
        Private Shared Function LazyFieldNameToFetchTag(fieldName As String) As Plugin.MetaDataType
            Dim mdt As MetaDataType
            If [Enum].TryParse(Of MetaDataType)(fieldName, True, mdt) AndAlso [Enum].IsDefined(GetType(MetaDataType), mdt) Then
                Return CType(mdt, Plugin.MetaDataType)
            End If
            Return View.FieldNameToMetaDataType(fieldName)
        End Function

        ' Container class for a level grouped by `field`. Mirrors the eager
        ' SetContainerClass mapping so clients render the right icon/category.
        Private Shared Function LazyContainerClassForField(field As String) As String
            Select Case field
                Case "AlbumArtist", "Artist" : Return "object.container.person.musicArtist"
                Case "Genre" : Return "object.container.genre.musicGenre"
                Case "Album", "AlbumArtistAndAlbum" : Return "object.container.album.musicAlbum"
                Case Else : Return "object.container"
            End Select
        End Function

        ' Distinct values of `targetTag` across files matching the accumulated filters.
        ' Sorted, case-insensitive-deduped; multi-value tags ("X; Y") contribute both.
        ' First call at root-depth scans the whole library (~6.5s on a 325k-track set per
        ' the lazy probe). Subsequent calls at any depth are typically sub-200ms because
        ' the filter narrows to a small subset. Cached per (filters, target) tuple.
        Private Shared Function GetLazyDistinctForFilter(filters As List(Of LazyFilter), targetTag As Plugin.MetaDataType, fieldName As String) As List(Of String)
            Return GetLazyDistinctForFilterIn("music", filters, targetTag, fieldName)
        End Function

        ' Logged wrapper for Library_QueryFilesEx in lazy code paths. Writes the verbatim
        ' query (smart-playlist XML or "domain=…" string) to UpnpErrorLog.dat - gated by
        ' Settings.LogDebugInfo - along with the result count. Tag is "LazyQuery" so the
        ' Read-last-query button on the Debug tab can grep for it.
        Private Shared Function LazyQueryFilesEx(query As String, ByRef urls() As String, context As String) As Boolean
            Dim ok As Boolean = mbApiInterface.Library_QueryFilesEx(query, urls)
            Dim count As Integer = If(urls Is Nothing, 0, urls.Length)
            ' Compact the query to a single line so it survives the line-oriented log.
            Dim flat As String = If(query, "").Replace(vbCrLf, " ").Replace(vbLf, " ").Replace(vbCr, " ").Replace(vbTab, " ")
            LogInformation("LazyQuery", "[" & context & "] returned=" & ok & " count=" & count & "  Q: " & flat)
            Return ok
        End Function

        ' fieldName is the human-readable level field ("AlbumArtist", "MusicBeeFolder",
        ' …). Synthetic fields (MusicBeeFolder → ExtraField1 slot) have no entry in
        ' Plugin.MetaDataType, so the in-memory dispatch resolves to a slot via Field-
        ' NameToMetaDataIndex instead of the targetTag round-trip.
        Private Shared Function GetLazyDistinctForFilterIn(endpoint As String, filters As List(Of LazyFilter), targetTag As Plugin.MetaDataType, fieldName As String) As List(Of String)
            If LazyEndpointIsInMemory(endpoint) Then Return GetLazyDistinctFromInMemory(endpoint, filters, fieldName)
            Dim cacheKey As String = endpoint & ":distinct:" & LazyFiltersToCacheKey(filters) & ":" & targetTag.ToString()
            Dim cached As List(Of String) = Nothing
            If lazyDistinctCache.TryGetValue(cacheKey, cached) Then Return cached
            Dim query As String = LazyBuildFilterQueryFor(endpoint, filters)
            Dim urls() As String = Nothing
            LazyQueryFilesEx(query, urls, "Distinct " & endpoint & " → " & targetTag.ToString())
            Dim setOfValues As New SortedSet(Of String)(StringComparer.CurrentCultureIgnoreCase)
            If urls IsNot Nothing Then
                For Each url As String In urls
                    Dim raw As String = mbApiInterface.Library_GetFileTag(url, targetTag)
                    If String.IsNullOrEmpty(raw) Then Continue For
                    For Each piece As String In raw.Split(New String() {"; "}, StringSplitOptions.RemoveEmptyEntries)
                        Dim trimmed As String = piece.Trim()
                        If trimmed.Length > 0 Then setOfValues.Add(trimmed)
                    Next
                Next
            End If
            Dim list As New List(Of String)(setOfValues)
            lazyDistinctCache(cacheKey) = list
            Return list
        End Function

        ' Cache-key signature for an album group-by spec (field names only - the cached list is
        ' identity, not order, so direction is irrelevant to it).
        Private Shared Function AlbumGroupCacheKey(fields As Plugin.AlbumGroupField()) As String
            Dim sb As New StringBuilder()
            For Each f As Plugin.AlbumGroupField In fields
                sb.Append(f.Field).Append("|"c)
            Next
            Return sb.ToString()
        End Function

        ' Composite-album entries across files matching the accumulated filters. Each entry's
        ' identity is the tuple of album group-by field values (taken WHOLE, not split on "; ").
        ' Cached unsorted by (group-by signature + filter signature); callers sort via
        ' SortLazyAlbums. The representative track is registered in fileLookup so album artwork
        ' resolves on first browse.
        Private Shared Function GetLazyAlbumsForFilterIn(endpoint As String, filters As List(Of LazyFilter), albumGroupBy As Plugin.AlbumGroupField()) As List(Of LazyAlbumEntry)
            Dim fields() As Plugin.AlbumGroupField = NormalizeAlbumGroupBy(albumGroupBy)
            If LazyEndpointIsInMemory(endpoint) Then Return GetLazyAlbumsFromInMemory(endpoint, filters, fields)
            Dim cacheKey As String = endpoint & ":albums:" & AlbumGroupCacheKey(fields) & ":" & LazyFiltersToCacheKey(filters)
            Dim cached As List(Of LazyAlbumEntry) = Nothing
            If lazyAlbumsCache.TryGetValue(cacheKey, cached) Then Return cached
            Dim query As String = LazyBuildFilterQueryFor(endpoint, filters)
            Dim urls() As String = Nothing
            LazyQueryFilesEx(query, urls, "Albums " & endpoint)
            Dim mdts(fields.Length - 1) As Plugin.MetaDataType
            For i As Integer = 0 To fields.Length - 1
                mdts(i) = LazyFieldNameToFetchTag(fields(i).Field)
            Next
            Dim byKey As New Dictionary(Of String, LazyAlbumEntry)(StringComparer.OrdinalIgnoreCase)
            If urls IsNot Nothing Then
                For Each u As String In urls
                    Dim raws(fields.Length - 1) As String
                    Dim disp(fields.Length - 1) As String
                    Dim allEmpty As Boolean = True
                    For i As Integer = 0 To fields.Length - 1
                        Dim v As String = ""
                        Try
                            v = mbApiInterface.Library_GetFileTag(u, mdts(i))
                        Catch
                        End Try
                        raws(i) = If(v, "")
                        disp(i) = AlbumKeyDisplayValue(fields(i).Field, raws(i))
                        If raws(i).Length > 0 Then allEmpty = False
                    Next
                    If allEmpty Then Continue For
                    Dim key As String = String.Join(ChrW(31), raws)
                    If Not byKey.ContainsKey(key) Then
                        ' Register the representative track in fileLookup so the /thumbnail/{id}
                        ' endpoint (fileLookup → MB Library_GetArtworkUrl) resolves on first browse.
                        SyncLock fileLookup
                            LoadFile(u)
                        End SyncLock
                        Dim keyPairs As New List(Of LazyFilter)
                        For i As Integer = 0 To fields.Length - 1
                            keyPairs.Add(New LazyFilter With {.Field = fields(i).Field, .Value = raws(i)})
                        Next
                        byKey.Add(key, New LazyAlbumEntry With {.Title = AlbumKeyTitle(fields, disp), .Key = keyPairs, .TrackUrl = u})
                    End If
                Next
            End If
            Dim list As New List(Of LazyAlbumEntry)(byKey.Values)
            lazyAlbumsCache(cacheKey) = list
            Return list
        End Function

        ' Sort an album list by the composite album group-by key, each field using its own
        ' direction (e.g. {Year↓, Album↑}). Returns a NEW list so the cache (stored unsorted)
        ' isn't mutated by callers with different sort prefs. albumGroupBy is index-aligned to
        ' each entry's Key.
        Private Shared Function SortLazyAlbums(unsorted As List(Of LazyAlbumEntry), albumGroupBy As Plugin.AlbumGroupField()) As List(Of LazyAlbumEntry)
            Dim fields() As Plugin.AlbumGroupField = NormalizeAlbumGroupBy(albumGroupBy)
            Dim sorted As New List(Of LazyAlbumEntry)(unsorted)
            sorted.Sort(Function(x, y)
                            Dim n As Integer = Math.Min(fields.Length, Math.Min(x.Key.Count, y.Key.Count))
                            For i As Integer = 0 To n - 1
                                Dim c As Integer = CompareAlbumKeyValue(x.Key(i).Value, y.Key(i).Value)
                                If fields(i).SortDescending Then c = -c
                                If c <> 0 Then Return c
                            Next
                            Return 0
                        End Function)
            Return sorted
        End Function

        ' (display name, resource identifier) pair for a lazy filter or playlist entry.
        ' The id is what gets URL-encoded into the child object id; the name is what the
        ' user sees in BubbleUPnP.
        Private NotInheritable Class LazyResourceEntry
            Public Name As String
            Public Id As String
        End Class

        ' Enumerate .xautopf filter files from MusicBee's Filters directory. Cheap -
        ' directory listing only, no XML parsing or library query. Sorted by name.
        ' Id is the basename (no extension, no path) - that's what View.GetBinding
        ' uses ("filter:<basename>" matches DiscoverEndpoints' endpoint id).
        ' includeHidden: browsing honours each filter's Exposed flag (a hidden filter is not
        ' advertised as a folder), but the Random-source picker must NOT - scoping random to a
        ' filter is unrelated to whether you want that filter cluttering the browse tree, and
        ' silently omitting it from the picker reads as "the filter is missing".
        Private Shared Function EnumerateLazyFilters(Optional includeHidden As Boolean = False) As List(Of LazyResourceEntry)
            Dim list As New List(Of LazyResourceEntry)
            Try
                Dim filtersDir As String = LazyFiltersDir()
                If IO.Directory.Exists(filtersDir) Then
                    Dim files() As String = IO.Directory.GetFiles(filtersDir, "*.xautopf")
                    Array.Sort(files, StringComparer.CurrentCultureIgnoreCase)
                    For Each f As String In files
                        Dim displayName As String = IO.Path.GetFileNameWithoutExtension(f)
                        ' Honor the per-filter visibility flag (binding.Exposed) - a hidden
                        ' filter is not advertised. (The eager LoadLibraryFilters gated here
                        ' too; this lazy enumerator is the path that actually runs.)
                        If Not includeHidden Then
                            Dim b As EndpointBinding = View.GetBinding("filter:" & displayName)
                            If b IsNot Nothing AndAlso Not b.Exposed Then Continue For
                        End If
                        list.Add(New LazyResourceEntry With {.Name = displayName, .Id = displayName})
                    Next
                End If
            Catch ex As Exception
                LogError(ex, "EnumerateLazyFilters")
            End Try
            Return list
        End Function

        ' Filter basenames offered as a Random source in the settings dialog. Returns plain
        ' strings (the .xautopf basename, which is exactly what Settings.RandomSourceFilter
        ' stores) so the dialog never touches the private LazyResourceEntry type.
        Friend Shared Function RandomSourceFilterNames() As List(Of String)
            Dim names As New List(Of String)
            For Each e As LazyResourceEntry In EnumerateLazyFilters(includeHidden:=True)
                If e IsNot Nothing AndAlso Not String.IsNullOrEmpty(e.Id) Then names.Add(e.Id)
            Next
            Return names
        End Function

        Private Shared Function LazyFiltersDir() As String
            Return IO.Path.Combine( _
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _
                "MusicBee", "Filters")
        End Function

        ' Extract the user's .xautopf conditions in a form ready to inline into our
        ' single flat <Conditions CombineMethod="All"> wrapper alongside the drill.
        '
        ' Key MB quirk (discovered empirically - undocumented):
        '   • MB DOES NOT honor a nested <Conditions CombineMethod="Any"> wrapped inside
        '     an outer <Conditions CombineMethod="All">. When such nesting is sent via
        '     Library_QueryFilesEx, MB ignores the outer All and our drill condition is
        '     silently dropped. So <Conditions>-as-OR-group at the wire level is dead.
        '   • MB DOES honor OR alternation expressed AS A CHILD OF A <Condition>, using
        '     <Or CombineMethod="Any"> inside the Condition element. e.g.:
        '         <Condition Field="A" Comparison="Is" Value="x">
        '             <Or CombineMethod="Any">
        '                 <Condition Field="B" Comparison="Is" Value="y" />
        '             </Or>
        '         </Condition>
        '     reads as "A=x OR B=y". This survives being embedded in an outer All block.
        '
        ' So when the user's top-level Conditions block is "Any" with two-or-more children,
        ' we REFORMULATE it: take the first child as the primary Condition, then move every
        ' subsequent child into an <Or CombineMethod="Any"> appended to the primary. The
        ' resulting single Condition (with its Or-children) preserves OR semantics in the
        ' MB-compatible form. The user's actual .xautopf on disk is NOT modified - this is
        ' purely a query-time rewrite.
        '
        ' "All" filters keep the existing flat-list extraction (one <Condition .../> per
        ' top-level child, all ANDed). Anything beyond that (deeper sub-groups, mixed
        ' nesting) falls back to flat extraction and may produce wrong narrowing - but
        ' that pattern is rare in user filters; document and revisit if it bites.
        Private Shared Function ExtractFilterConditionsBlock(xautopf As String) As String
            If String.IsNullOrEmpty(xautopf) Then Return ""
            Try
                Dim doc As XDocument = XDocument.Parse(xautopf)
                Dim conditions As XElement = doc.Descendants("Conditions").FirstOrDefault()
                If conditions Is Nothing Then Return ""
                Dim combineAttr As XAttribute = conditions.Attribute("CombineMethod")
                Dim combine As String = If(combineAttr Is Nothing, "All", combineAttr.Value)
                Dim children As List(Of XElement) = conditions.Elements("Condition").ToList()
                If children.Count = 0 Then Return ""
                If String.Equals(combine, "Any", StringComparison.OrdinalIgnoreCase) AndAlso children.Count >= 2 Then
                    ' Reformulate Any → primary Condition + <Or> alternatives. We work on
                    ' clones so we never mutate the parsed XDocument's tree (paranoia).
                    Dim primary As XElement = New XElement(children(0))
                    Dim orElement As New XElement("Or", New XAttribute("CombineMethod", "Any"))
                    For i As Integer = 1 To children.Count - 1
                        orElement.Add(New XElement(children(i)))
                    Next
                    primary.Add(orElement)
                    Return primary.ToString(SaveOptions.DisableFormatting)
                End If
                ' "All" (or "Any" with a single child - degenerate, treat as a lone AND).
                ' Flat-concatenate top-level Conditions; their children (any pre-existing
                ' <Or> elements, etc.) ride along verbatim and remain MB-compatible.
                Dim sb As New StringBuilder
                For Each cond As XElement In children
                    sb.Append(cond.ToString(SaveOptions.DisableFormatting))
                Next
                Return sb.ToString()
            Catch ex As Exception
                LogError(ex, "ExtractFilterConditionsBlock")
                Return ""
            End Try
        End Function

        ' Cache the parsed filter conditions block per filter basename so repeated drills
        ' don't re-read and re-parse the .xautopf on every Browse.
        Private Shared ReadOnly lazyFilterConditionsCache As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        Private Shared Function GetFilterBaseConditions(filterBasename As String) As String
            Dim cached As String = Nothing
            If lazyFilterConditionsCache.TryGetValue(filterBasename, cached) Then Return cached
            Dim result As String = ""
            Try
                Dim filePath As String = IO.Path.Combine(LazyFiltersDir(), filterBasename & ".xautopf")
                If IO.File.Exists(filePath) Then
                    result = ExtractFilterConditionsBlock(IO.File.ReadAllText(filePath))
                End If
            Catch ex As Exception
                LogError(ex, "GetFilterBaseConditions", filterBasename)
            End Try
            lazyFilterConditionsCache(filterBasename) = result
            Return result
        End Function

        ' One row in a playlist folder listing - either a sub-folder (drill stays in
        ' playlists endpoint with the deeper path) or a leaf playlist (links to the
        ' playlist endpoint with its URL).
        Private NotInheritable Class LazyPlaylistTreeNode
            Public IsFolder As Boolean
            Public Name As String        ' display name (just the segment, not full path)
            Public FolderPath As String  ' for folders: cumulative path
            Public PlaylistUrl As String ' for leaves: MB playlist URL
        End Class

        ' Build the immediate children for one level of the playlist folder tree.
        ' currentFolder = "" → root level; otherwise the cumulative folder path so far.
        ' Walks every playlist returned by MB, looks at the segment(s) past currentFolder,
        ' and partitions into folders (deduped) and leaf playlists. Folders sort first,
        ' both alphabetical.
        Private Shared Function BuildPlaylistFolderListing(currentFolder As String) As List(Of LazyPlaylistTreeNode)
            Dim result As New List(Of LazyPlaylistTreeNode)
            Dim folderSet As New SortedDictionary(Of String, String)(StringComparer.CurrentCultureIgnoreCase)
            Dim leaves As New List(Of LazyPlaylistTreeNode)
            Dim prefix As String = If(String.IsNullOrEmpty(currentFolder), "", currentFolder & "\")
            For Each entry As LazyResourceEntry In EnumerateLazyPlaylists()
                Dim fullName As String = entry.Name
                If String.IsNullOrEmpty(fullName) Then Continue For
                If prefix.Length > 0 AndAlso Not fullName.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase) Then Continue For
                Dim suffix As String = If(prefix.Length > 0, fullName.Substring(prefix.Length), fullName)
                If suffix.Length = 0 Then Continue For
                Dim slashIdx As Integer = suffix.IndexOf("\"c)
                If slashIdx >= 0 Then
                    Dim segment As String = suffix.Substring(0, slashIdx)
                    Dim cumulative As String = If(prefix.Length > 0, currentFolder & "\" & segment, segment)
                    If Not folderSet.ContainsKey(segment) Then
                        folderSet.Add(segment, cumulative)
                    End If
                Else
                    leaves.Add(New LazyPlaylistTreeNode With {.IsFolder = False, .Name = suffix, .PlaylistUrl = entry.Id})
                End If
            Next
            For Each kv As KeyValuePair(Of String, String) In folderSet
                result.Add(New LazyPlaylistTreeNode With {.IsFolder = True, .Name = kv.Key, .FolderPath = kv.Value})
            Next
            leaves.Sort(Function(x, y) StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name))
            result.AddRange(leaves)
            Return result
        End Function

        ' Enumerate playlists known to MusicBee via the Playlist_Query* iterator. The
        ' resource Id is the playlist URL (path on disk) - what Playlist_QueryFilesEx
        ' needs to fetch the file list. Display name is taken from Playlist_GetName.
        ' Excludes Radio playlists (PlaylistFormat.Radio) which surface elsewhere.
        Private Shared Function EnumerateLazyPlaylists() As List(Of LazyResourceEntry)
            Dim list As New List(Of LazyResourceEntry)
            Try
                mbApiInterface.Playlist_QueryPlaylists()
                Do
                    Dim url As String = mbApiInterface.Playlist_QueryGetNextPlaylist()
                    If url Is Nothing Then Exit Do
                    If mbApiInterface.Playlist_GetType(url) = PlaylistFormat.Radio Then Continue Do
                    Dim name As String = mbApiInterface.Playlist_GetName(url)
                    ' Honor the per-playlist visibility flag. Bindings are keyed by full name
                    ' ("playlist:" & Playlist_GetName), matching View.DiscoverEndpoints.
                    Dim b As EndpointBinding = View.GetBinding("playlist:" & name)
                    If b IsNot Nothing AndAlso Not b.Exposed Then Continue Do
                    list.Add(New LazyResourceEntry With {.Name = name, .Id = url})
                Loop
            Catch ex As Exception
                LogError(ex, "EnumerateLazyPlaylists")
            End Try
            list.Sort(Function(x, y) StringComparer.CurrentCultureIgnoreCase.Compare(x.Name, y.Name))
            Return list
        End Function

        ' URL-list cache for filters and playlists. Keyed by "filter:<path>" or
        ' "playlist:<url>". Cleared by SetLibraryDirty when MB notifies of a mutation.
        ' Separated from the (heavier) tag cache so paginated browses re-use the cheap
        ' URL list and only LoadFile the visible slice.
        Private Shared ReadOnly lazyResourceUrls As New Dictionary(Of String, String())(StringComparer.OrdinalIgnoreCase)

        ' Sliced track fetch: cache the URL list per resource, then LoadFile only the
        ' [startingIndex .. startingIndex+count) slice. Returns (slice, total) so the
        ' caller can emit DIDL for the slice and report the full count to BubbleUPnP.
        Private Function GetLazyTracksSliceForFilterFile(filterPath As String, startingIndex As Integer, requestedCount As Integer, ByRef total As Integer) As List(Of String())
            Dim slice As New List(Of String())
            total = 0
            Try
                If String.IsNullOrEmpty(filterPath) OrElse Not IO.File.Exists(filterPath) Then Return slice
                Dim cacheKey As String = "filter:" & filterPath
                Dim urls() As String = Nothing
                If Not lazyResourceUrls.TryGetValue(cacheKey, urls) OrElse urls Is Nothing Then
                    Dim xml As String = IO.File.ReadAllText(filterPath)
                    LazyQueryFilesEx(xml, urls, "FilterFile " & filterPath)
                    If urls Is Nothing Then urls = New String() {}
                    lazyResourceUrls(cacheKey) = urls
                End If
                total = urls.Length
                Dim endingIndex As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                If endingIndex < startingIndex Then Return slice
                SyncLock fileLookup
                    For i As Integer = startingIndex To endingIndex
                        slice.Add(LoadFile(urls(i)))
                    Next
                End SyncLock
            Catch ex As Exception
                LogError(ex, "GetLazyTracksSliceForFilterFile", filterPath)
            End Try
            Return slice
        End Function

        ' Playlist equivalent - URL list cached per playlist, slice-fetched for tags.
        Private Function GetLazyTracksSliceForPlaylist(playlistUrl As String, startingIndex As Integer, requestedCount As Integer, ByRef total As Integer) As List(Of String())
            Dim slice As New List(Of String())
            total = 0
            Try
                Dim cacheKey As String = "playlist:" & playlistUrl
                Dim urls() As String = Nothing
                If Not lazyResourceUrls.TryGetValue(cacheKey, urls) OrElse urls Is Nothing Then
                    mbApiInterface.Playlist_QueryFilesEx(playlistUrl, urls)
                    If urls Is Nothing Then urls = New String() {}
                    lazyResourceUrls(cacheKey) = urls
                End If
                total = urls.Length
                Dim endingIndex As Integer = Math.Min(startingIndex + requestedCount - 1, total - 1)
                If endingIndex < startingIndex Then Return slice
                SyncLock fileLookup
                    For i As Integer = startingIndex To endingIndex
                        slice.Add(LoadFile(urls(i)))
                    Next
                End SyncLock
            Catch ex As Exception
                LogError(ex, "GetLazyTracksSliceForPlaylist", playlistUrl)
            End Try
            Return slice
        End Function

        ' Track tag arrays for files matching the accumulated filters. LoadFile populates
        ' fileLookup so the streaming endpoint can resolve these tracks for playback.
        ' Sorted disc-then-track via the shared AlbumFileComparer.
        Private Function GetLazyTracksForFilter(filters As List(Of LazyFilter)) As List(Of String())
            Return GetLazyTracksForFilterIn("music", filters)
        End Function

        Private Function GetLazyTracksForFilterIn(endpoint As String, filters As List(Of LazyFilter)) As List(Of String())
            If LazyEndpointIsInMemory(endpoint) Then Return GetLazyTracksFromInMemory(endpoint, filters)
            Dim query As String = LazyBuildFilterQueryFor(endpoint, filters)
            Dim urls() As String = Nothing
            LazyQueryFilesEx(query, urls, "Tracks " & endpoint)
            Dim list As New List(Of String())
            If urls IsNot Nothing Then
                SyncLock fileLookup
                    For Each u As String In urls
                        list.Add(LoadFile(u))
                    Next
                End SyncLock
            End If
            list.Sort(New AlbumFileComparer)
            Return list
        End Function

        ' Emit a single container element. Lean version of WriteContainerDIDL with just the
        ' fields a lazy browse needs (no album metadata, no artwork) - keep it minimal until
        ' a chunk needs more.
        Private Sub WriteLazyContainer(writer As XmlWriter, id As String, parentId As String, title As String, containerClass As String, childCount As String, Optional hostUrl As String = Nothing, Optional filterSet As HashSet(Of String) = Nothing, Optional artworkSourceUrl As String = Nothing)
            writer.WriteStartElement("container")
            writer.WriteAttributeString("id", id)
            writer.WriteAttributeString("parentID", parentId)
            writer.WriteAttributeString("restricted", "1")
            writer.WriteAttributeString("searchable", "1")
            If childCount.Length > 0 Then
                writer.WriteAttributeString("childCount", childCount)
            End If
            writer.WriteElementString("dc", "title", Nothing, SanitizeXmlText(title))
            writer.WriteElementString("upnp", "class", Nothing, containerClass)
            ' Album-level artwork. Mirrors the eager path in WriteContainerDIDL: emit one
            ' albumArtURI per supported thumbnail size, each pointing at our /thumbnail/{id}
            ' HTTP endpoint which resolves via Library_GetArtworkUrl. Skipped when no source
            ' URL was provided (non-album containers, or albums where the cache wasn't
            ' built with a representative track URL).
            ' Album-level artwork. Three resolution paths, tried in order:
            '   1. Podcast subscription by Album title - handles the ValueDistinct case
            '      where the user's binding groups podcasts by Album (no track URL is
            '      passed because the level emits abstract distinct values, not real
            '      tracks). Lookup is name → subId, then point at /PodcastThumbnail/.
            '   2. Podcast subscription by representative track fileId - handles the
            '      AlbumLayer case (Leaf=AT) where a TrackUrl IS provided.
            '   3. Library_GetArtworkUrl(trackUrl, -2) - the normal music library case.
            ' All three feed the same albumArtURI emit loop; only the URL differs.
            Dim isAlbumContainer As Boolean = (containerClass IsNot Nothing) AndAlso containerClass.Contains("musicAlbum")
            Dim canEmitArt As Boolean = Not String.IsNullOrEmpty(hostUrl) AndAlso _
                                         streamingProfile.PictureSize > 0 AndAlso _
                                         (filterSet Is Nothing OrElse filterSet.Contains("upnp:albumArtURI"))
            If canEmitArt AndAlso isAlbumContainer Then
                Try
                    Dim podSubIdByName As String = Nothing
                    Dim podSubIdByFile As String = Nothing
                    Dim trackId As String = ""
                    Dim hasTitleMatch As Boolean = podcastSubIdByAlbumName.TryGetValue(title, podSubIdByName)
                    Dim hasFileMatch As Boolean = False
                    If Not String.IsNullOrEmpty(artworkSourceUrl) Then
                        trackId = GetFileId(artworkSourceUrl)
                        hasFileMatch = podcastSubIdByFileId.TryGetValue(trackId, podSubIdByFile)
                    End If
                    Dim libraryArtUrl As String = If(String.IsNullOrEmpty(artworkSourceUrl), "0", mbApiInterface.Library_GetArtworkUrl(artworkSourceUrl, -2))
                    Dim hasLibraryArt As Boolean = (libraryArtUrl <> "0")
                    Dim hasAnyArt As Boolean = hasTitleMatch OrElse hasFileMatch OrElse hasLibraryArt
                    If hasAnyArt Then
                        For Each size As String In New String() {"JPEG_TN", "JPEG_SM"}
                            If size = "JPEG_TN" OrElse streamingProfile.PictureSize = 160 Then
                                writer.WriteStartElement("upnp", "albumArtURI", Nothing)
                                writer.WriteAttributeString("dlna", "profileID", "urn:schemas-dlna-org:metadata-1-0/", size)
                                Dim sized As String = If(size = "JPEG_TN" AndAlso streamingProfile.PictureSize <> 160, streamingProfile.PictureSize.ToString("0000000"), size)
                                Dim url As String
                                If hasTitleMatch Then
                                    url = String.Format("{0}/PodcastThumbnail/{1}", hostUrl, Uri.EscapeDataString(PodcastSlug(podSubIdByName)))
                                ElseIf hasFileMatch Then
                                    url = String.Format("{0}/PodcastThumbnail/{1}", hostUrl, Uri.EscapeDataString(PodcastSlug(podSubIdByFile)))
                                Else
                                    url = String.Format("{0}/thumbnail/{1}.{2}", hostUrl, trackId, sized)
                                End If
                                writer.WriteValue(url)
                                writer.WriteEndElement()
                            End If
                        Next size
                    End If
                Catch ex As Exception
                    LogError(ex, "WriteLazyContainer.Artwork", "title=" & title)
                End Try
            End If
            writer.WriteEndElement()
        End Sub

        ' Strip characters that aren't valid in XML 1.0 text content. Real-world MB tag
        ' values occasionally contain control chars (U+001F unit separator was seen in an
        ' artist name) which XmlWriter refuses to emit. Drop them rather than entitise:
        ' they're invisible in any client UI anyway. Valid set per XML 1.0 spec:
        '   U+0009, U+000A, U+000D, U+0020-U+D7FF, U+E000-U+FFFD, U+10000-U+10FFFF
        Private Shared Function SanitizeXmlText(s As String) As String
            If String.IsNullOrEmpty(s) Then Return s
            Dim needsFix As Boolean = False
            For Each c As Char In s
                Dim code As Integer = AscW(c)
                If code < &H20 AndAlso code <> &H9 AndAlso code <> &HA AndAlso code <> &HD Then
                    needsFix = True
                    Exit For
                End If
                If code >= &HD800 AndAlso code <= &HDFFF Then
                    needsFix = True
                    Exit For
                End If
                If code = &HFFFE OrElse code = &HFFFF Then
                    needsFix = True
                    Exit For
                End If
            Next
            If Not needsFix Then Return s
            Dim sb As New StringBuilder(s.Length)
            For Each c As Char In s
                Dim code As Integer = AscW(c)
                Dim ok As Boolean = False
                If code = &H9 OrElse code = &HA OrElse code = &HD Then ok = True
                If code >= &H20 AndAlso code <= &HD7FF Then ok = True
                If code >= &HE000 AndAlso code <= &HFFFD Then ok = True
                If ok Then sb.Append(c)
            Next
            Return sb.ToString()
        End Function

        ' Session cache of composite-album entries per (group-by + filter) signature. The
        ' representative URL serves as the artwork source when emitting album containers.
        ' Cleared by SetLibraryDirty when MB notifies of a tag mutation.
        Private Shared ReadOnly lazyAlbumsCache As New Dictionary(Of String, List(Of LazyAlbumEntry))(StringComparer.OrdinalIgnoreCase)

        Private NotInheritable Class LazyAlbumEntry
            Public Title As String              ' composite display title (field values joined " - ")
            ' Ordered (field, value) pairs = the album's composite identity. Same order as the
            ' path's album group-by, so SortLazyAlbums applies each field's direction by index,
            ' and EmitLazyItem appends them to the child id for exact drill-in re-querying.
            Public Key As List(Of LazyFilter)
            Public TrackUrl As String           ' any one track in the album - used to fetch artwork
        End Class

        ' Minimal XML-attribute escape - five named entities are the entire XML 1.0 spec.
        ' Used when building smart-playlist XML filters from tag values that may contain
        ' &, <, >, ", or '. Not the same as SanitizeXmlText, which strips invalid control
        ' chars; this one assumes the input is already XML-text-valid and just protects
        ' attribute delimiters.
        Private Shared Function XmlAttributeEscape(s As String) As String
            If String.IsNullOrEmpty(s) Then Return s
            Return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("""", "&quot;").Replace("'", "&apos;")
        End Function

        ' Map an endpoint id to a QueryFilesEx domain query.
        '
        ' KNOWN-WEIRD: MusicBee's query parser in this version rejects single-domain queries
        ' like "domain=Music" (probe confirmed: returns False, 0 files). Only the legacy
        ' multi-domain form "domain=Music+AudioBooks+Inbox" actually works. So even for the
        ' Music endpoint we currently fetch the broad set and accept that audiobook/inbox
        ' tracks contaminate the lazy distinct-value list. Future chunk: use a smart-playlist
        ' XML filter on Category to cleanly partition (the probe showed XML queries work).
        Private Shared Function LazyEndpointDomainQuery(endpoint As String) As String
            Return "domain=Music+AudioBooks+Inbox"
        End Function

        ' yaiol - Lazy in-memory endpoint dispatch.
        ' ─────────────────────────────────────────────────────────────────────────────
        ' Music + filter:* endpoints fetch their data via smart-playlist XML queries to
        ' MusicBee at every drill level. That works because Library_QueryFilesEx can be
        ' filtered with arbitrary <Conditions>. The four endpoints below CAN'T use that
        ' path:
        '   • Audiobook / Inbox - Library_QueryFilesEx accepts "domain=AudioBooks" /
        '     "domain=Inbox" at the root, but smart-playlist filtering on these domains
        '     isn't reliable in this MB build, so per-drill MB queries are unsafe.
        '   • Radio - single-pass enumeration via "domain=Radio" plus a "MusicBee folder"
        '     attribute that lives in the Category slot (LoadRadioFiles copies it to
        '     ExtraField1). No structural hierarchy in MB beyond that.
        '   • Podcast - Podcasts_* API is a completely separate world; episodes are
        '     synthesised into tag arrays by LoadPodcastFiles, never reachable via
        '     Library_QueryFilesEx.
        ' So for these four we eagerly populate a List(Of String()) the first time the
        ' endpoint is browsed, and the lazy hierarchy walker drives off that list in
        ' memory (DistinctFromInMemory / AlbumsFromInMemory / TracksFromInMemory). Per-
        ' endpoint loaded flags ensure the eager fetch runs once per session; SetLibrary-
        ' Dirty clears them so the next browse re-fetches. The lists themselves are
        ' bounded (audiobooks/podcasts/radio/inbox are typically hundreds, not 100k+),
        ' so building distinct-value sets in-memory is cheap.
        Private Shared audiobookFilesLoaded As Boolean = False
        Private Shared inboxFilesLoaded As Boolean = False
        Private Shared radioFilesLoaded As Boolean = False
        Private Shared podcastFilesLoaded As Boolean = False

        ' Returns True iff the endpoint draws from an in-memory tag list rather than from
        ' per-drill MB queries.
        Private Shared Function LazyEndpointIsInMemory(endpoint As String) As Boolean
            If String.IsNullOrEmpty(endpoint) Then Return False
            Select Case endpoint.ToLowerInvariant()
                Case "audiobook", "inbox", "radio", "podcast"
                    Return True
            End Select
            Return False
        End Function

        ' Lazy fetch the file list for the endpoint and return it. Loaded once per session,
        ' invalidated by SetLibraryDirty. Returns empty list (not Nothing) on failure so
        ' callers can iterate unconditionally.
        Private Shared Function EnsureLazyEndpointInMemory(endpoint As String) As List(Of String())
            Select Case endpoint.ToLowerInvariant()
                Case "audiobook"
                    If Not audiobookFilesLoaded Then
                        LoadAudiobookFiles()
                        audiobookFilesLoaded = True
                        LogInformation("LazyQuery", "[Ensure audiobook] post-load files=" & If(audiobookFiles Is Nothing, -1, audiobookFiles.Count))
                    End If
                    Return If(audiobookFiles, New List(Of String()))
                Case "inbox"
                    If Not inboxFilesLoaded Then
                        LoadInboxFiles()
                        inboxFilesLoaded = True
                        LogInformation("LazyQuery", "[Ensure inbox] post-load files=" & If(inboxFiles Is Nothing, -1, inboxFiles.Count))
                    End If
                    Return If(inboxFiles, New List(Of String()))
                Case "radio"
                    If Not radioFilesLoaded Then
                        LoadRadioFiles()
                        radioFilesLoaded = True
                        LogInformation("LazyQuery", "[Ensure radio] post-load files=" & If(radioFiles Is Nothing, -1, radioFiles.Count))
                    End If
                    Return If(radioFiles, New List(Of String()))
                Case "podcast"
                    If Not podcastFilesLoaded Then
                        LoadPodcastFiles()
                        podcastFilesLoaded = True
                        LogInformation("LazyQuery", "[Ensure podcast] post-load files=" & If(podcastFiles Is Nothing, -1, podcastFiles.Count))
                    End If
                    Return If(podcastFiles, New List(Of String()))
            End Select
            Return New List(Of String())
        End Function

        ' Test whether one track satisfies every drill filter accumulated so far. Mirrors
        ' the semantics of the smart-playlist Comparison="Is" used on the MB-query path:
        ' multi-value tags ("; "-joined) match if ANY element equals the filter value (case-
        ' insensitive). Letter-bucket / _AT pseudo-filters are dropped by RealFiltersOnly.
        Private Shared Function MatchesFiltersInMemory(tags() As String, filters As List(Of LazyFilter)) As Boolean
            Dim real As List(Of LazyFilter) = RealFiltersOnly(filters)
            For Each f As LazyFilter In real
                Dim mdi As MetaDataIndex = FieldNameToMetaDataIndex(f.Field)
                Dim raw As String = ""
                If CInt(mdi) > 0 AndAlso CInt(mdi) < tags.Length Then raw = If(tags(CInt(mdi)), "")
                If String.IsNullOrEmpty(f.Value) Then
                    ' Empty filter value = "this tag is empty for the track". Album grouping
                    ' collapses tracks with no value for a group-by field (e.g. an untagged
                    ' YearOnly, common in the inbox) under the empty key, so drilling into
                    ' such an album emits an empty-valued filter. It must MATCH the empty
                    ' tag, not reject it (the old unconditional empty-raw → False made every
                    ' no-year inbox album browse to zero tracks). A non-empty tag fails it.
                    If Not String.IsNullOrEmpty(raw) Then Return False
                Else
                    If String.IsNullOrEmpty(raw) Then Return False
                    Dim hit As Boolean = False
                    For Each piece As String In raw.Split(New String() {"; "}, StringSplitOptions.RemoveEmptyEntries)
                        If String.Equals(piece.Trim(), f.Value, StringComparison.CurrentCultureIgnoreCase) Then
                            hit = True
                            Exit For
                        End If
                    Next
                    If Not hit Then Return False
                End If
            Next
            Return True
        End Function

        ' Distinct values of `targetField` across the in-memory file list, narrowed to
        ' tracks matching the accumulated filters. Multi-value tags contribute each piece.
        ' Sort + dedupe via case-insensitive SortedSet so the result matches the MB-query
        ' variant byte-for-byte at the level above.
        ' fieldName goes straight through FieldNameToMetaDataIndex which knows about
        ' synthetic slots ("MusicBeeFolder" → ExtraField1). The Plugin.MetaDataType
        ' round-trip used on the MB-query path can't represent synthetics (they're not
        ' in MB's enum) so it would return slot -1 here - broken for radio/podcast
        ' bindings that group by MusicBeeFolder.
        Private Shared Function GetLazyDistinctFromInMemory(endpoint As String, filters As List(Of LazyFilter), fieldName As String) As List(Of String)
            Dim cacheKey As String = endpoint & ":distinct:" & LazyFiltersToCacheKey(filters) & ":" & fieldName
            Dim cached As List(Of String) = Nothing
            If lazyDistinctCache.TryGetValue(cacheKey, cached) Then Return cached
            Dim files As List(Of String()) = EnsureLazyEndpointInMemory(endpoint)
            Dim targetIdx As MetaDataIndex = FieldNameToMetaDataIndex(fieldName)
            Dim setOfValues As New SortedSet(Of String)(StringComparer.CurrentCultureIgnoreCase)
            Dim matchedFiles As Integer = 0
            If CInt(targetIdx) > 0 Then
                For Each tags As String() In files
                    If Not MatchesFiltersInMemory(tags, filters) Then Continue For
                    matchedFiles += 1
                    If CInt(targetIdx) >= tags.Length Then Continue For
                    Dim raw As String = If(tags(CInt(targetIdx)), "")
                    If raw.Length = 0 Then Continue For
                    For Each piece As String In raw.Split(New String() {"; "}, StringSplitOptions.RemoveEmptyEntries)
                        Dim trimmed As String = piece.Trim()
                        If trimmed.Length > 0 Then setOfValues.Add(trimmed)
                    Next
                Next
            End If
            Dim list As New List(Of String)(setOfValues)
            lazyDistinctCache(cacheKey) = list
            LogInformation("LazyQuery", "[Distinct InMem " & endpoint & "] field=" & fieldName & " targetIdx=" & CInt(targetIdx) & " files=" & files.Count & " matchedFiles=" & matchedFiles & " distinctValues=" & list.Count)
            Return list
        End Function

        ' Composite-album list across the in-memory file list narrowed to the accumulated
        ' filters. Mirrors GetLazyAlbumsForFilterIn's contract (cached unsorted by group-by +
        ' filter signature; caller sorts via SortLazyAlbums). Has full tag arrays, so it builds
        ' the composite key with the same helpers as the eager engine.
        Private Shared Function GetLazyAlbumsFromInMemory(endpoint As String, filters As List(Of LazyFilter), albumGroupBy As Plugin.AlbumGroupField()) As List(Of LazyAlbumEntry)
            Dim fields() As Plugin.AlbumGroupField = NormalizeAlbumGroupBy(albumGroupBy)
            Dim cacheKey As String = endpoint & ":albums:" & AlbumGroupCacheKey(fields) & ":" & LazyFiltersToCacheKey(filters)
            Dim cached As List(Of LazyAlbumEntry) = Nothing
            If lazyAlbumsCache.TryGetValue(cacheKey, cached) Then Return cached
            Dim files As List(Of String()) = EnsureLazyEndpointInMemory(endpoint)
            Dim byKey As New Dictionary(Of String, LazyAlbumEntry)(StringComparer.OrdinalIgnoreCase)
            For Each tags As String() In files
                If Not MatchesFiltersInMemory(tags, filters) Then Continue For
                Dim raws(fields.Length - 1) As String
                Dim disp(fields.Length - 1) As String
                Dim allEmpty As Boolean = True
                For i As Integer = 0 To fields.Length - 1
                    raws(i) = AlbumKeyRawValue(tags, fields(i).Field)
                    disp(i) = AlbumKeyDisplayValue(fields(i).Field, raws(i))
                    If raws(i).Length > 0 Then allEmpty = False
                Next
                If allEmpty Then Continue For
                Dim key As String = String.Join(ChrW(31), raws)
                If Not byKey.ContainsKey(key) Then
                    Dim keyPairs As New List(Of LazyFilter)
                    For i As Integer = 0 To fields.Length - 1
                        keyPairs.Add(New LazyFilter With {.Field = fields(i).Field, .Value = raws(i)})
                    Next
                    byKey.Add(key, New LazyAlbumEntry With {.Title = AlbumKeyTitle(fields, disp), .Key = keyPairs, .TrackUrl = tags(MetaDataIndex.Url)})
                End If
            Next
            Dim list As New List(Of LazyAlbumEntry)(byKey.Values)
            lazyAlbumsCache(cacheKey) = list
            Return list
        End Function

        ' Track tag arrays from the in-memory file list narrowed to tracks matching the
        ' accumulated filters. Sorted disc-then-track via AlbumFileComparer to match the
        ' MB-query variant.
        Private Shared Function GetLazyTracksFromInMemory(endpoint As String, filters As List(Of LazyFilter)) As List(Of String())
            Dim files As List(Of String()) = EnsureLazyEndpointInMemory(endpoint)
            Dim list As New List(Of String())
            For Each tags As String() In files
                If MatchesFiltersInMemory(tags, filters) Then list.Add(tags)
            Next
            list.Sort(New AlbumFileComparer)
            Return list
        End Function

        Public Sub Browse(headers As Dictionary(Of String, String), objectId As String, browseType As BrowseFlag, filter As String, startingIndex As Integer, requestedCount As Integer, sortCriteria As String, ByRef result As String, ByRef numberReturned As String, ByRef totalMatches As String)
            'Debug.WriteLine(objectId & "," & browseType.ToString & "," & startingIndex & "," & requestedCount & "," & sortCriteria & "," & filter)
            If Settings.LogDebugInfo Then
                LogInformation("Browse", objectId & "," & browseType.ToString() & "," & startingIndex & "," & requestedCount & ",sort=" & sortCriteria)
            End If
            Dim host As String
            Dim hostUrl As String = If(Not headers.TryGetValue("host", host), "", "http://" & host)
            Dim text As New StringBuilder(16384)
            Dim filterSet As HashSet(Of String) = Nothing
            If filter <> "*" Then
                filterSet = New HashSet(Of String)(filter.Split(New Char() {","c}, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase)
            End If
            ' --- Lazy browse routing ---
            ' IDs prefixed with "L:" are handled by LazyBrowse, which fetches data on demand
            ' from MB rather than relying on the eager LoadLibrary tree. Nothing emits L:
            ' IDs yet (Chunk 1 scaffolding only), so this short-circuit is currently dead
            ' code on the path. Once endpoints start emitting L: IDs in their child lists,
            ' this routes them here without triggering the full library load.
            If objectId IsNot Nothing AndAlso objectId.StartsWith("L:", StringComparison.Ordinal) Then
                LazyBrowse(hostUrl, filterSet, objectId, startingIndex, requestedCount, result, numberReturned, totalMatches)
                Return
            End If
            ' --- Lazy-search album result routing ---
            ' "Ssrch_alb_<n>" containers are the album results we emit from HandleLazySearch.
            ' When the user taps one in BubbleUPnP, BubbleUPnP issues a BrowseDirectChildren
            ' on the synthetic ID - we serve the tracks we cached in searchAlbumResultTracks
            ' at search time. BrowseMetadata is also routed here so clients that fetch the
            ' container's metadata first don't fall through to the generic "(not matched)"
            ' path that would otherwise return empty.
            If objectId IsNot Nothing AndAlso objectId.StartsWith("Ssrch_alb_", StringComparison.OrdinalIgnoreCase) Then
                Dim cached As List(Of String()) = Nothing
                SyncLock searchAlbumResultTracks
                    searchAlbumResultTracks.TryGetValue(objectId, cached)
                End SyncLock
                Dim albumTextBuf As New StringBuilder(8192)
                Dim albumXmlSettings As New XmlWriterSettings With {.OmitXmlDeclaration = True, .Indent = False}
                Using albumWriter As XmlWriter = XmlWriter.Create(albumTextBuf, albumXmlSettings)
                    albumWriter.WriteStartElement("DIDL-Lite", "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/")
                    albumWriter.WriteAttributeString("xmlns", "dc", Nothing, "http://purl.org/dc/elements/1.1/")
                    albumWriter.WriteAttributeString("xmlns", "upnp", Nothing, "urn:schemas-upnp-org:metadata-1-0/upnp/")
                    albumWriter.WriteAttributeString("xmlns", "pv", Nothing, "http://www.pv.com/pvns/")
                    If cached Is Nothing OrElse cached.Count = 0 Then
                        numberReturned = "0"
                        totalMatches = "0"
                    ElseIf browseType = BrowseFlag.BrowseMetadata Then
                        Dim albumTitle As String = cached(0)(MetaDataIndex.Album)
                        WriteContainerDIDL(albumWriter, hostUrl, filterSet, objectId, "0", cached.Count.ToString(), albumTitle, "object.container.album.musicAlbum", cached)
                        numberReturned = "1"
                        totalMatches = "1"
                    Else
                        Dim emittedTracks As Integer = WriteAudioFilesDIDL(albumWriter, hostUrl, filterSet, "object.item.audioItem.musicTrack", objectId, cached, startingIndex, requestedCount)
                        numberReturned = emittedTracks.ToString()
                        totalMatches = cached.Count.ToString()
                    End If
                    albumWriter.WriteEndElement()
                End Using
                result = albumTextBuf.ToString()
                Return
            End If
            If Not fileLookupLoaded Then
                ' needed because WMP can load Playlists without starting from the music file root
                LoadLibrary()
            End If
            Dim xmlSettings As New XmlWriterSettings With {
                .OmitXmlDeclaration = True
            }
            Using writer As XmlWriter = XmlWriter.Create(text, xmlSettings)
                writer.WriteStartElement("DIDL-Lite", "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/")
                writer.WriteAttributeString("xmlns", "dc", Nothing, "http://purl.org/dc/elements/1.1/")
                writer.WriteAttributeString("xmlns", "upnp", Nothing, "urn:schemas-upnp-org:metadata-1-0/upnp/")
                'writer.WriteAttributeString("xmlns", "av", Nothing, "urn:schemas-sony-com:av")
                writer.WriteAttributeString("xmlns", "pv", Nothing, "http://www.pv.com/pvns/")
                ' Search-result virtual album IDs: "Salb<idx>" → resolve via the album index.
                ' Used by clients (BubbleUPnP) drilling into a Random-Albums result.
                If objectId IsNot Nothing AndAlso objectId.StartsWith("Salb", StringComparison.Ordinal) Then
                    Dim albumIdx As Integer
                    BuildSearchAlbumIndex()
                    If Integer.TryParse(objectId.Substring(4), albumIdx) AndAlso albumIdx >= 0 AndAlso albumIdx < searchAlbumKeys.Count Then
                        Dim albumKey As String = searchAlbumKeys(albumIdx)
                        Dim tracks As List(Of String()) = searchAlbumTracks(albumKey)
                        Dim title As String = tracks(0)(MetaDataIndex.Album)
                        If browseType = BrowseFlag.BrowseMetadata Then
                            WriteContainerDIDL(writer, hostUrl, filterSet, objectId, "0", tracks.Count.ToString(), title, "object.container.album.musicAlbum", tracks)
                            totalMatches = "1"
                            numberReturned = "1"
                        Else
                            numberReturned = WriteAudioFilesDIDL(writer, hostUrl, filterSet, "object.item.audioItem.musicTrack", objectId, tracks, startingIndex, requestedCount).ToString()
                            totalMatches = tracks.Count.ToString()
                        End If
                    Else
                        totalMatches = "0"
                        numberReturned = "0"
                    End If
                ElseIf browseType = BrowseFlag.BrowseMetadata Then
                    If objectId = "0" Then
                        LoadLibrary()
                        LoadNode(template, tree, True)
                        WriteContainerDIDL(writer, hostUrl, filterSet, "0", "-1", tree.Folders.Length.ToString(), "Root", "object.container", Nothing)
                        totalMatches = "1"
                        numberReturned = "1"
                    ElseIf objectId.Length < 4 OrElse objectId.IndexOf("_"c) <> -1 Then
                        Dim objectIds() As String = objectId.Split("_"c)
                        Dim node As TemplateNode
                        Dim folder As FolderNode
                        Dim lookupIdCount As Integer
                        Dim matched As Boolean = TryLocateNode(objectIds, node, folder, lookupIdCount)
                        If Not matched Then
                            totalMatches = "0"
                            numberReturned = "0"
                        Else
                            Dim containerClassOverride0 As String = Nothing
                            Dim containerClassOverride1 As String = Nothing
                            If node.Fields IsNot Nothing Then
                                Dim fieldIndex As Integer = node.Fields.Length - 1
                                If lookupIdCount = 1 Then
                                    If node.Fields.Length > 1 Then
                                        fieldIndex -= 1
                                    End If
                                Else
                                    Do While lookupIdCount >= 2
                                        Dim index As Integer
                                        If Not Integer.TryParse(objectIds(objectIds.Length - (lookupIdCount - 1)), index) OrElse index >= folder.Folders.Length Then
                                            LogInformation("Browse", "id count=" & lookupIdCount & ",index=" & index & ",folders=" & folder.Folders.Length)
                                            Throw New ArgumentException
                                        Else
                                            folder = folder.Folders(index)
                                        End If
                                        lookupIdCount -= 1
                                    Loop
                                End If
                                SetContainerClass(node, (folder.IsBucket AndAlso objectIds.Length <= 3), fieldIndex, containerClassOverride0, containerClassOverride1)
                            End If
                            Dim firstIndex As Boolean = (objectId.EndsWith("_0", StringComparison.Ordinal) AndAlso folder.Name = "[All Tracks]")
                            WriteContainerDIDL(writer, hostUrl, filterSet, objectId, "0", If(folder.Folders Is Nothing, If(folder.ChildFiles Is Nothing, 0, folder.ChildFiles.Count), folder.Folders.Length).ToString(), folder.Name, If(containerClassOverride0 Is Nothing, node.ContainerClass, If(firstIndex, containerClassOverride0, containerClassOverride1)), folder.ChildFiles)
                            totalMatches = "1"
                            numberReturned = "1"
                        End If
                    Else
                        Dim charIndex As Integer = objectId.IndexOf("."c)
                        If charIndex <> -1 Then
                            objectId = objectId.Substring(0, charIndex)
                        End If
                        SyncLock fileLookup
                            Dim tags() As String
                            If Not fileLookup.TryGetValue(objectId, tags) Then
                                LogInformation("Browse", "metadata lookup fail=" & objectId)
                                totalMatches = "0"
                                numberReturned = "0"
                            Else
                                WriteAudioFileDIDL(writer, hostUrl, filterSet, "0", tags, "object.item.audioItem.musicTrack")
                                totalMatches = "1"
                                numberReturned = "1"
                            End If
                        End SyncLock
                    End If
                ElseIf objectId = "0" Then
                    ' browse with children
                    If startingIndex = 0 Then
                        ' allow library to be reloaded if starting from root
                        LoadLibrary()
                        LoadNode(template, tree, True)
                    End If
                    numberReturned = WriteContainerItemsDIDL(writer, hostUrl, filterSet, objectId, template, tree, startingIndex, requestedCount).ToString()
                    totalMatches = tree.Folders.Length.ToString()
                Else
                    Dim objectIds() As String = objectId.Split("_"c)
                    Dim node As TemplateNode
                    Dim folder As FolderNode
                    Dim lookupIdCount As Integer
                    Dim matched As Boolean = TryLocateNode(objectIds, node, folder, lookupIdCount)
                    If Settings.LogDebugInfo Then
                        LogInformation("Browse", "entering: " & If(matched, """" & If(folder.Name, "") & """", "(not matched)") & "  id=" & objectId)
                    End If
                    If Not matched Then
                        totalMatches = "0"
                        numberReturned = "0"
                    ElseIf node.Category = ContainerCategory.Audiobook Then
                        ' audiobooks
                        folder.ChildFiles.Sort(New AlbumFileComparer)
                        numberReturned = WriteAudioFilesDIDL(writer, hostUrl, filterSet, "object.item.audioItem.audioBook", objectId, folder.ChildFiles, startingIndex, requestedCount).ToString()
                        totalMatches = folder.ChildFiles.Count.ToString()
                    ElseIf node.Category = ContainerCategory.Inbox Then
                        ' inbox
                        folder.ChildFiles.Sort(New AlbumFileComparer)
                        numberReturned = WriteAudioFilesDIDL(writer, hostUrl, filterSet, "object.item.audioItem.musicTrack", objectId, folder.ChildFiles, startingIndex, requestedCount).ToString()
                        totalMatches = folder.ChildFiles.Count.ToString()
                    ElseIf node.Category = ContainerCategory.Radio AndAlso (folder.Folders Is Nothing OrElse folder.Folders.Length = 0) Then
                        ' Radio (flat - single-path binding with empty hierarchy).
                        ' Hierarchical Radio (binding has at least one grouping field) has
                        ' folder.Folders populated by BuildHierarchicalFolderNode and falls
                        ' through to the generic container branch below; the leaf-emission
                        ' path then picks audioBroadcast via node.Category.
                        ' Two specifics vs the generic file-list branch:
                        '   1. NO per-call sort (see history below).
                        '   2. UPnP class is "audioBroadcast" not "musicTrack" - semantically
                        '      correct for live continuous streams AND used as the signal in
                        '      WriteAudioFileDIDL to bypass transcoding when
                        '      Settings.ForceNativeStreamForRadio is on.
                        ' Sort-history: previously fell through to the generic branch which
                        ' called files.Sort(AlbumFileComparer) on EVERY browse call. Radio
                        ' entries all have empty Album/Disc/Track tags, so the sort keys all
                        ' tie → List.Sort is unstable → different order per call. BubbleUPnP
                        ' fetches Radio in two paginated calls (0..15, then 16..end); between
                        ' the calls the order reshuffled, so some stations appeared in BOTH
                        ' pages (duplicates) and some in NEITHER (missing). radioFiles is now
                        ' sorted once at load time by Title.
                        numberReturned = WriteAudioFilesDIDL(writer, hostUrl, filterSet, "object.item.audioItem.audioBroadcast", objectId, folder.ChildFiles, startingIndex, requestedCount).ToString()
                        totalMatches = folder.ChildFiles.Count.ToString()
                    ElseIf lookupIdCount = 0 OrElse (node.Category = ContainerCategory.Playlist AndAlso lookupIdCount < 2) Then
                        numberReturned = WriteContainerItemsDIDL(writer, hostUrl, filterSet, objectId, node, folder, startingIndex, requestedCount).ToString()
                        totalMatches = folder.Folders.Length.ToString()
                    Else
                        Dim index As Integer
                        Do While lookupIdCount >= 2
                            If Not Integer.TryParse(objectIds(objectIds.Length - (lookupIdCount - 1)), index) OrElse index >= folder.Folders.Length Then
                                LogInformation("Browse", "id count=" & lookupIdCount & ",index=" & index & ",folders=" & folder.Folders.Length)
                                Throw New ArgumentException
                            Else
                                folder = folder.Folders(index)
                            End If
                            lookupIdCount -= 1
                        Loop
                        If Settings.LogDebugInfo Then
                            LogInformation("Browse", "drilled to: """ & If(folder.Name, "") & """")
                        End If
                        If folder.Folders IsNot Nothing AndAlso folder.Folders.Length > 0 Then
                            numberReturned = WriteContainerItemsDIDL(writer, hostUrl, filterSet, objectId, node, folder, startingIndex, requestedCount).ToString()
                            totalMatches = folder.Folders.Length.ToString()
                        ElseIf node.Category = ContainerCategory.Playlist Then
                            Dim files As List(Of String()) = folder.ChildFiles
                            If files Is Nothing Then
                                Dim playlistUrl As String = folder.Path
                                files = New List(Of String())
                                Dim filenames() As String = Nothing
                                If folder.Path = "NowPlaying" Then
                                    mbApiInterface.NowPlayingList_QueryFilesEx(Nothing, filenames)
                                Else
                                    mbApiInterface.Playlist_QueryFilesEx(playlistUrl, filenames)
                                End If
                                If filenames IsNot Nothing Then
                                    For filenameIndex As Integer = 0 To filenames.Count - 1
                                        files.Add(LoadFile(filenames(filenameIndex)))
                                    Next filenameIndex
                                End If
                                folder.ChildFiles = files
                            End If
                            numberReturned = WriteAudioFilesDIDL(writer, hostUrl, filterSet, "object.item.audioItem.musicTrack", objectId, files, startingIndex, requestedCount).ToString()
                            totalMatches = files.Count.ToString()
                        Else
                            Dim files As List(Of String()) = folder.ChildFiles
                            If Not node.IncludeAllTracks OrElse index > 0 Then
                                files.Sort(New AlbumFileComparer)
                            Else
                                files.Sort(New TrackNameFileComparer)
                            End If
                            ' Radio leaves render as audioBroadcast so ForceNativeStreamForRadio
                            ' and clients distinguishing streams from tracks work correctly.
                            Dim leafClass As String = If(node.Category = ContainerCategory.Radio, _
                                                         "object.item.audioItem.audioBroadcast", _
                                                         "object.item.audioItem.musicTrack")
                            numberReturned = WriteAudioFilesDIDL(writer, hostUrl, filterSet, leafClass, objectId, files, startingIndex, requestedCount).ToString()
                            totalMatches = files.Count.ToString()
                        End If
                    End If
                End If
                writer.WriteEndElement()
            End Using
            result = text.ToString()
            'If Settings.LogDebugInfo Then
            '    LogInformation("Browse", "num=" & numberReturned & ",tot=" & totalMatches & ",res=" & result)
            'End If
            'Debug.WriteLine(result)
        End Sub

        Private Function TryLocateNode(objectIds() As String, ByRef node As TemplateNode, ByRef folder As FolderNode, ByRef lookupIdCount As Integer) As Boolean
            node = template
            folder = tree
            lookupIdCount = 0
            Dim matched As Boolean = False
            For index As Integer = 0 To objectIds.Length - 1
                matched = False
                If node.Fields Is Nothing Then
                    For childIndex As Integer = 0 To node.ChildNodes.Length - 1
                        If node.ChildNodes(childIndex).Id = objectIds(index) Then
                            matched = True
                            node = node.ChildNodes(childIndex)
                            folder = folder.Folders(childIndex)
                            Exit For
                        End If
                    Next childIndex
                Else
                    Dim offset As Integer
                    If Integer.TryParse(objectIds(index), offset) AndAlso offset < folder.Folders.Length Then
                        matched = True
                        If Not folder.IsBucket Then
                            ' use last node as first could be [All Tracks]
                            node = node.ChildNodes(node.ChildNodes.Count - 1)
                        End If
                        folder = folder.Folders(offset)
                    End If
                End If
                If Not matched Then
                    Exit For
                Else
                    LoadNode(node, folder, True)
                    If node.ChildNodes Is Nothing Then
                        lookupIdCount = (objectIds.Length - index)
                        Exit For
                    End If
                End If
            Next index
            Return matched
        End Function

        Private Function WriteContainerItemsDIDL(writer As XmlWriter, hostUrl As String, filterSet As HashSet(Of String), parentId As String, template As TemplateNode, folder As FolderNode, startingIndex As Integer, requestedCount As Integer) As Integer
            Dim folders() As FolderNode = folder.Folders
            If startingIndex >= folders.Length Then
                Return 0
            Else
                Dim endingIndex As Integer = startingIndex + requestedCount - 1
                If endingIndex >= folders.Length Then
                    endingIndex = folders.Length - 1
                End If
                Dim containerClassOverride0 As String = Nothing
                Dim containerClassOverride1 As String = Nothing
                If template.Fields IsNot Nothing Then
                    SetContainerClass(template, folder.IsBucket, template.Fields.Length - 1, containerClassOverride0, containerClassOverride1)
                End If
                For index As Integer = startingIndex To endingIndex
                    Dim containerClass As String = If(template.Category = ContainerCategory.Playlist AndAlso folder.Folders(index).Folders.Length > 0, "object.container", If(containerClassOverride0 Is Nothing, template.ChildNodes(index).ContainerClass, If(index = 0 AndAlso String.Compare(folder.Folders(0).Name, "[All Tracks]", StringComparison.Ordinal) = 0, containerClassOverride0, containerClassOverride1)))
                    ' Wired-playlist album-leaf rescue: the generic playlistsTree uses Fields={Url}
                    ' which makes SetContainerClass emit "playlistContainer" for every child level
                    ' under a playlist - including the album-leaf we built via WirePlaylistBindings.
                    ' UPnP clients render playlistContainer as a generic playlist icon. Detect the
                    ' "this child is actually an album of tracks" shape (no sub-folders, has track
                    ' files) and emit musicAlbum so cover art shows. Filter container classes are
                    ' driven by proper Fields so they don't hit this branch.
                    If containerClass = "object.container.playlistContainer" Then
                        Dim child As FolderNode = folder.Folders(index)
                        Dim noSubFolders As Boolean = (child.Folders Is Nothing OrElse child.Folders.Length = 0)
                        Dim hasTracks As Boolean = (child.ChildFiles IsNot Nothing AndAlso child.ChildFiles.Count > 0)
                        If noSubFolders AndAlso hasTracks Then
                            containerClass = "object.container.album.musicAlbum"
                        End If
                    End If
                    WriteContainerDIDL(writer, hostUrl, filterSet, If(template.Fields IsNot Nothing, parentId & "_" & index, template.ChildNodes(index).Path), parentId, If(folders(index).Folders Is Nothing, If(folders(index).ChildFiles Is Nothing, 0, folders(index).ChildFiles.Count), folders(index).Folders.Length).ToString(), folders(index).Name, containerClass, folders(index).ChildFiles)
                Next index
                Return (endingIndex - startingIndex + 1)
            End If
        End Function

        Private Sub SetContainerClass(template As TemplateNode, isBucket As Boolean, fieldIndex As Integer, ByRef containerClassOverride0 As String, ByRef containerClassOverride1 As String)
            If isBucket Then
                containerClassOverride1 = "object.container"
            Else
                containerClassOverride1 = template.ContainerClass
                Dim field As MetaDataIndex = template.Fields(fieldIndex)
                Select Case field
                    Case MetaDataIndex.Album, MetaDataIndex.AlbumArtistAndAlbum
                        containerClassOverride1 = "object.container.album.musicAlbum"
                    Case MetaDataIndex.Artist, MetaDataIndex.AlbumArtist
                        containerClassOverride1 = "object.container.person.musicArtist"
                    Case MetaDataIndex.Genre
                        containerClassOverride1 = "object.container.genre.musicGenre"
                    Case MetaDataIndex.Url
                        containerClassOverride1 = "object.container.playlistContainer"
                End Select
            End If
            If Not template.IncludeAllTracks Then
                containerClassOverride0 = containerClassOverride1
            Else
                containerClassOverride0 = "object.container.musicContainer"
            End If
        End Sub

        Private Sub WriteContainerDIDL(writer As XmlWriter, hostUrl As String, filterSet As HashSet(Of String), id As String, parentID As String, childCount As String, title As String, containerClass As String, childFiles As List(Of String()))
            writer.WriteStartElement("container")
            writer.WriteAttributeString("id", id)
            writer.WriteAttributeString("restricted", "1")
            writer.WriteAttributeString("parentID", parentID)
            If containerClass <> "object.container.playlistContainer" AndAlso childCount.Length > 0 Then
                writer.WriteAttributeString("childCount", childCount)
            End If
            writer.WriteAttributeString("searchable", "1")
            writer.WriteElementString("dc", "title", Nothing, title)
            If containerClass = "object.container.album.musicAlbum" AndAlso childFiles IsNot Nothing AndAlso childFiles.Count > 0 Then
                Dim tags() As String = childFiles(0)
                Dim year As String = tags(MetaDataIndex.Year)
                If year.Length = 4 Then
                    writer.WriteElementString("dc", "date", Nothing, year & "-01-01")
                ElseIf year.Length > 4 Then
                    Dim yearValue As DateTime
                    If DateTime.TryParse(year, yearValue) Then
                        writer.WriteElementString("dc", "date", Nothing, yearValue.ToString("yyyy-MM-dd"))
                    End If
                End If
                writer.WriteStartElement("upnp", "artist", Nothing)
                writer.WriteAttributeString("role", "AlbumArtist")
                writer.WriteValue(tags(MetaDataIndex.AlbumArtist))
                writer.WriteEndElement()
                writer.WriteElementString("upnp", "albumArtist", Nothing, tags(MetaDataIndex.AlbumArtist))
                Dim composer As String = tags(MetaDataIndex.Composer)
                If composer.Length > 0 Then
                    writer.WriteStartElement("upnp", "author", Nothing)
                    writer.WriteAttributeString("role", "Composer")
                    writer.WriteValue(composer)
                    writer.WriteEndElement()
                End If
                writer.WriteElementString("upnp", "album", Nothing, tags(MetaDataIndex.Album))
                If tags(MetaDataIndex.Genre).Length > 0 Then
                    writer.WriteElementString("upnp", "genre", Nothing, tags(MetaDataIndex.Genre))
                End If
                ' Album-level thumbnail. Use the first track's file id so the existing
                ' HTTP /thumbnail/{id} endpoint resolves the artwork via Library_GetArtworkUrl.
                If (filterSet Is Nothing OrElse filterSet.Contains("upnp:albumArtURI")) AndAlso hostUrl IsNot Nothing AndAlso streamingProfile.PictureSize > 0 Then
                    If mbApiInterface.Library_GetArtworkUrl(tags(MetaDataIndex.Url), -2) <> "0" Then
                        Dim trackId As String = GetFileId(tags(MetaDataIndex.Url))
                        For Each size As String In New String() {"JPEG_TN", "JPEG_SM"}
                            If size = "JPEG_TN" OrElse streamingProfile.PictureSize = 160 Then
                                writer.WriteStartElement("upnp", "albumArtURI", Nothing)
                                writer.WriteAttributeString("dlna", "profileID", "urn:schemas-dlna-org:metadata-1-0/", size)
                                writer.WriteValue(String.Format("{0}/thumbnail/{1}.{2}", hostUrl, trackId, If(size = "JPEG_TN" AndAlso streamingProfile.PictureSize <> 160, streamingProfile.PictureSize.ToString("0000000"), size)))
                                writer.WriteEndElement()
                            End If
                        Next size
                    End If
                End If
            End If
            writer.WriteElementString("upnp", "class", Nothing, containerClass)
            If filterSet IsNot Nothing AndAlso filterSet.Contains("av:mediaClass") Then   'writer.LookupPrefix("urn:schemas-sony-com:av") IsNot Nothing
                writer.WriteElementString("av", "mediaClass", "urn:schemas-sony-com:av", "M")
            End If
            writer.WriteEndElement()
        End Sub

        Private Function WriteAudioFilesDIDL(writer As XmlWriter, hostUrl As String, filterSet As HashSet(Of String), classType As String, parentId As String, files As List(Of String()), startingIndex As Integer, requestedCount As Integer) As Integer
            If startingIndex >= files.Count Then
                Return 0
            Else
                Dim endingIndex As Integer = startingIndex + requestedCount - 1
                If endingIndex >= files.Count Then
                    endingIndex = files.Count - 1
                End If
                For index As Integer = startingIndex To endingIndex
                    WriteAudioFileDIDL(writer, hostUrl, filterSet, parentId, files(index), classType)
                Next index
                Return (endingIndex - startingIndex + 1)
            End If
        End Function

        Public Function WriteAudioFileDIDL(writer As XmlWriter, hostUrl As String, url As String, streamHandle As Integer) As String
            Dim objectId As String = GetFileId(url)
            Dim tags() As String
            SyncLock fileLookup
                If Not fileLookup.TryGetValue(objectId, tags) Then
                    tags = LoadFile(url)
                End If
            End SyncLock
            writer.WriteStartElement("DIDL-Lite", "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/")
            writer.WriteAttributeString("xmlns", "dc", Nothing, "http://purl.org/dc/elements/1.1/")
            writer.WriteAttributeString("xmlns", "upnp", Nothing, "urn:schemas-upnp-org:metadata-1-0/upnp/")
            'writer.WriteAttributeString("xmlns", "av", Nothing, "urn:schemas-sony-com:av")
            writer.WriteAttributeString("xmlns", "pv", Nothing, "http://www.pv.com/pvns/")
            Dim httpUrl As String = WriteAudioFileDIDL(writer, hostUrl, Nothing, "0", tags, "object.item.audioItem.musicTrack", streamHandle, True)
            writer.WriteEndElement()
            Return httpUrl
        End Function

        Private Function WriteAudioFileDIDL(writer As XmlWriter, hostUrl As String, filterSet As HashSet(Of String), parentId As String, tags() As String, classType As String, Optional streamHandle As Integer = 0, Optional musicBeePlayToMode As Boolean = False) As String
            writer.WriteStartElement("item")
            Dim defaultHttpUrl As String = Nothing
            ' F33 - radio streams have no discrete tracks; MusicBee reports their `Kind` property
            ' ending in "Stream" (e.g. "MP3 Stream", "Internet Stream"). Forcing continuous-stream
            ' output for them regardless of the user's global toggle avoids the per-track
            ' start/stop dance that doesn't make sense for an infinite source. Only checked when
            ' MusicBee is driving playback (musicBeePlayToMode); library-fetch path is unaffected.
            Dim isRadioStream As Boolean = False
            If musicBeePlayToMode Then
                Dim sourceUrlForKind As String = tags(MetaDataIndex.Url)
                If Not String.IsNullOrEmpty(sourceUrlForKind) Then
                    Dim fileKind As String = mbApiInterface.Library_GetFileProperty(sourceUrlForKind, FilePropertyType.Kind)
                    isRadioStream = (fileKind IsNot Nothing AndAlso fileKind.EndsWith("Stream", StringComparison.OrdinalIgnoreCase))
                End If
            End If
            If musicBeePlayToMode AndAlso (Settings.ContinuousOutput OrElse isRadioStream) Then
                writer.WriteAttributeString("id", "continuousstream")
                writer.WriteAttributeString("restricted", "true")
                writer.WriteAttributeString("parentID", "0")
                writer.WriteElementString("upnp", "class", Nothing, classType)
                writer.WriteElementString("dc", "title", Nothing, "Continuous Stream")
                If filterSet Is Nothing OrElse filterSet.Contains("dc:creator") Then
                    writer.WriteElementString("dc", "creator", Nothing, "MusicBee")
                End If
                If filterSet Is Nothing OrElse filterSet.Contains("upnp:artist") Then
                    writer.WriteElementString("upnp", "artist", Nothing, "MusicBee")
                End If
                If filterSet Is Nothing OrElse filterSet.Any(Function(a) a.StartsWith("res")) Then
                    Dim forcedCodec As FileCodec = If(IsCodecSupported(FileCodec.Pcm), FileCodec.Pcm, FileCodec.Wave)
                    writer.WriteStartElement("res")
                    writer.WriteAttributeString("protocolInfo", String.Format("http-get:*:{0}:{1}", GetMime(forcedCodec, streamingProfile.TranscodeBitDepth), GetEncodeFeature(forcedCodec, True)))
                    Dim sampleRate As Integer = If(streamingProfile.TranscodeSampleRate = -1, If(44100 < streamingProfile.MinimumSampleRate, streamingProfile.MinimumSampleRate, 44100), streamingProfile.TranscodeSampleRate)
                    If filterSet Is Nothing OrElse filterSet.Contains("res@sampleFrequency") Then
                        writer.WriteAttributeString("sampleFrequency", sampleRate.ToString())
                    End If
                    If filterSet Is Nothing OrElse filterSet.Contains("res@bitsPerSample") Then
                        writer.WriteAttributeString("bitsPerSample", "16")
                    End If
                    If filterSet Is Nothing OrElse filterSet.Contains("res@nrAudioChannels") Then
                        writer.WriteAttributeString("nrAudioChannels", "2")
                    End If
                    If filterSet Is Nothing OrElse filterSet.Contains("res@bitrate") Then
                        ' F27 - UPnP DIDL `res@bitrate` is BYTES per second, not kbps. Was dividing by
                        ' 1000 (kbps) which made the value off by a factor of ~125 - devices that
                        ' allocated buffers from this value would stutter on the (apparently tiny) stream.
                        writer.WriteAttributeString("bitrate", ((sampleRate * 2 * 16) \ 8).ToString())
                    End If
                    defaultHttpUrl = String.Format("{0}/encode/continuousstream{1}.{2}", hostUrl, streamHandle.ToString(), GetMime(forcedCodec, 16).Substring(6))
                    writer.WriteValue(defaultHttpUrl)
                    writer.WriteEndElement()
                End If
            Else
                Dim url As String = tags(MetaDataIndex.Url)
                If url.EndsWith(".asx", StringComparison.OrdinalIgnoreCase) Then
                    url = mbApiInterface.Library_GetFileTag(url, Plugin.MetaDataType.Origin)
                End If
                Dim sourceFileExtension As String
                Dim sourceFileCodec As FileCodec
                Dim id As String = GetFileId(url)
                Dim isVirtualFile As Boolean = url.EndsWith("#"c)
                Dim isWebFile As Boolean = (url.IndexOf("://", StringComparison.Ordinal) <> -1)
                If isVirtualFile Then
                    ' remove track from virtual url
                    url = url.Substring(0, url.LastIndexOf("#"c, url.Length - 2))
                End If
                Dim charIndex As Integer = url.LastIndexOf("."c)
                If charIndex = -1 Then
                    sourceFileExtension = ""
                Else
                    sourceFileExtension = url.Substring(charIndex).ToLower()
                End If
                sourceFileCodec = GetCodec(sourceFileExtension)
                Dim channelCount As Integer
                Integer.TryParse(tags(MetaDataIndex.Channels), channelCount)
                Dim sampleRate As Integer
                Integer.TryParse(tags(MetaDataIndex.SampleRate), sampleRate)
                If sourceFileCodec = FileCodec.Unknown AndAlso streamHandle <> 0 Then
                    Bass.TryGetStreamInformation(streamHandle, sampleRate, channelCount, sourceFileCodec)
                End If
                Dim fileHasTrackGain As Boolean = Not String.IsNullOrEmpty(mbApiInterface.Library_GetFileProperty(url, FilePropertyType.ReplayGainTrack))
                Dim fileHasAlbumGain As Boolean = Not String.IsNullOrEmpty(mbApiInterface.Library_GetFileProperty(url, FilePropertyType.ReplayGainAlbum))
                Dim mbSoundEffectsActive As Boolean = (mbApiInterface.Player_GetDspEnabled() OrElse mbApiInterface.Player_GetEqualiserEnabled())
                Dim mbReplayGainActive As Boolean = (mbApiInterface.Player_GetReplayGainMode() <> ReplayGainMode.Off AndAlso (fileHasTrackGain OrElse fileHasAlbumGain))
                Dim forceEncode As Boolean
                ' F46/F47 - accumulate the reason(s) we're transcoding so the log line at the end of
                ' the decision chain can explain *why* (instead of users seeing CPU spikes on a file
                ' they expected to stream natively and having to guess which option caused it).
                Dim transcodeReason As String = ""
                If musicBeePlayToMode Then
                    ' force encoding so an equaliser/ DSP can be added mid-stream?
                    forceEncode = (mbSoundEffectsActive OrElse mbReplayGainActive OrElse isWebFile)
                    If mbSoundEffectsActive Then transcodeReason &= "MB-DSP/EQ;"
                    If mbReplayGainActive Then transcodeReason &= "MB-ReplayGain;"
                    If isWebFile Then transcodeReason &= "WebFile;"
                Else
                    forceEncode = ((streamingProfile.EnableSoundEffects AndAlso mbSoundEffectsActive) OrElse (streamingProfile.EnableReplayGain AndAlso mbReplayGainActive))
                    If streamingProfile.EnableSoundEffects AndAlso mbSoundEffectsActive Then transcodeReason &= "Profile-DSP/EQ;"
                    If streamingProfile.EnableReplayGain AndAlso mbReplayGainActive Then transcodeReason &= "Profile-ReplayGain;"
                End If
                If isVirtualFile Then
                    forceEncode = True
                    transcodeReason &= "VirtualFile;"
                End If
                ' F4 - per-profile override (was global pre-v9 schema): force every stream through
                ' the transcoder. F37: takes precedence over F3 (force-native-stream) when both are
                ' somehow True at the same time, because forcing IS the more aggressive instruction -
                ' a user who ticked both clearly wants transcoding. UI guards against this via mutual
                ' exclusion (toggling one off-ticks the other) but the runtime guard handles any
                ' loaded-from-disk inconsistency.
                If streamingProfile.ForceTranscoding Then
                    forceEncode = True
                    transcodeReason &= "ForceTranscoding;"
                ElseIf streamingProfile.ForceNativeStream Then
                    ' F3 - per-profile override: send original file bytes; ignore DSP/ReplayGain/sample-rate decisions.
                    forceEncode = False
                    transcodeReason = ""  ' reset - native stream wins
                End If
                If sampleRate < streamingProfile.MinimumSampleRate Then
                    forceEncode = True
                    transcodeReason &= "SampleRate<" & streamingProfile.MinimumSampleRate & ";"
                    sampleRate = streamingProfile.MinimumSampleRate
                ElseIf sampleRate > streamingProfile.MaximumSampleRate Then
                    forceEncode = True
                    transcodeReason &= "SampleRate>" & streamingProfile.MaximumSampleRate & ";"
                    sampleRate = streamingProfile.MaximumSampleRate
                End If
                If streamingProfile.StereoOnly AndAlso channelCount <> 2 Then
                    forceEncode = True
                    transcodeReason &= "DownmixToStereo;"
                    channelCount = 2
                End If
                Dim forcedCodec As FileCodec = FileCodec.Unknown
                ' F3 - when force-native-stream is on (and F4 isn't overriding), skip the entire transcoding-decision chain.
                ' The renderer gets the file natively regardless of whether it advertises codec support.
                ' F37: bypass the codec/bandwidth decision only when ForceNativeStream wins outright.
                ' Per-profile ForceTranscoding now sits in the same precedence as ForceNativeStream,
                ' and its higher-priority branch above already set forceEncode=True - but we also
                ' have to ensure bypassTranscodeDecision doesn't skip the actual codec selection.
                ' Detect radio entries via the DIDL class. The Radio container's Browse branch
                ' emits "object.item.audioItem.audioBroadcast" for every item, so any tag set
                ' rendered with that class type is a station - independent of musicBeePlayToMode
                ' or the source URL/extension. Combined with Settings.ForceNativeStreamForRadio,
                ' this bypasses the entire transcode decision for radio: ship the source codec
                ' bytes as-is, regardless of profile, instead of decoding+re-encoding to L16 PCM
                ' (which multiplies bandwidth ~20× and breaks playback on some renderers).
                Dim isRadioBroadcast As Boolean = (classType IsNot Nothing AndAlso classType.EndsWith("audioBroadcast", StringComparison.OrdinalIgnoreCase))
                Dim bypassTranscodeDecision As Boolean = (streamingProfile.ForceNativeStream AndAlso Not streamingProfile.ForceTranscoding) _
                    OrElse (isRadioBroadcast AndAlso Settings.ForceNativeStreamForRadio)
                If isRadioBroadcast AndAlso Settings.ForceNativeStreamForRadio Then
                    forceEncode = False
                    transcodeReason = ""
                End If
                If Not bypassTranscodeDecision Then
                    If forceEncode OrElse Not IsCodecSupported(sourceFileCodec) Then
                        forcedCodec = streamingProfile.TranscodeCodec
                        ' F47 - explicit log when the device doesn't advertise the source codec.
                        If Not forceEncode AndAlso Not IsCodecSupported(sourceFileCodec) Then
                            transcodeReason &= "DeviceLacksCodec(" & sourceFileCodec.ToString() & ");"
                        End If
                    ElseIf Settings.BandwidthConstrained AndAlso (".flac;.wv;.tak;.wav;.aiff;.pcm;".IndexOf(sourceFileExtension) <> -1 OrElse (String.Compare(sourceFileExtension, ".m4a", StringComparison.OrdinalIgnoreCase) = 0 AndAlso mbApiInterface.Library_GetFileProperty(url, FilePropertyType.Kind).StartsWith("ALAC ", StringComparison.OrdinalIgnoreCase))) Then
                        forceEncode = True
                        forcedCodec = streamingProfile.TranscodeCodec
                        transcodeReason &= "BandwidthConstrained;"
                    End If
                End If
                ' F46/F47 - single log line per track explaining transcode/native decision.
                ' Only fires when Settings.LogDebugInfo is on (it would be too chatty in normal logs).
                If Settings.LogDebugInfo AndAlso musicBeePlayToMode Then
                    If forcedCodec = FileCodec.Unknown Then
                        LogInformation("StreamDecision", "native " & sourceFileCodec.ToString() & " - " & url)
                    Else
                        LogInformation("StreamDecision", "transcode " & sourceFileCodec.ToString() & "→" & forcedCodec.ToString() & " reason=" & If(transcodeReason.Length = 0, "(none?)", transcodeReason) & " - " & url)
                    End If
                End If
                If forcedCodec = FileCodec.Pcm Then
                    If streamingProfile.DoNotUseRawPcm Then
                        ' F6 - bypass raw PCM, use Wave (PCM-in-RIFF) instead. For devices that mishandle audio/L16 or audio/L24.
                        forcedCodec = FileCodec.Wave
                    ElseIf Not musicBeePlayToMode Then
                        forcedCodec = FileCodec.AnyPcm
                    Else
                        forcedCodec = If(Not IsCodecSupported(FileCodec.Wave) OrElse (IsCodecSupported(FileCodec.Pcm) AndAlso tags(MetaDataIndex.Size) = "0"), FileCodec.Pcm, FileCodec.Wave)
                    End If
                End If
                'If musicBeePlayToMode Then
                '    LogInformation("Item", "transcode=" & forceEncode & " to=" & forcedCodec.ToString() & ",rgmode=" & mbApiInterface.Player_GetReplayGainMode().ToString() & ",gain=" & (fileHasTrackGain OrElse fileHasAlbumGain) & ",eq/dsp=" & mbSoundEffectsActive & ",samplerate=" & sampleRate & ",dev min=" & streamingProfile.MinimumSampleRate & ",dev max=" & streamingProfile.MaximumSampleRate & ",chans=" & channelCount & ",dev stereo=" & streamingProfile.StereoOnly & ",codec=" & sourceFileCodec.ToString & ",sup=" & IsCodecSupported(sourceFileCodec) & ",bw=" & Settings.BandwidthConstrained)
                'End If
                If forcedCodec = FileCodec.Unknown AndAlso streamHandle <> 0 Then
                    Bass.CloseStream(streamHandle)
                    streamHandle = 0
                End If
                writer.WriteAttributeString("id", id)
                writer.WriteAttributeString("restricted", "true")
                writer.WriteAttributeString("parentID", parentId)
                writer.WriteElementString("upnp", "class", Nothing, classType)
                writer.WriteElementString("dc", "title", Nothing, tags(MetaDataIndex.Title))
                If filterSet Is Nothing OrElse filterSet.Contains("dc:date") Then
                    Dim year As String = tags(MetaDataIndex.Year)
                    If year.Length = 4 Then
                        writer.WriteElementString("dc", "date", Nothing, year & "-01-01")
                    ElseIf year.Length > 4 Then
                        Dim yearValue As DateTime
                        If DateTime.TryParse(year, yearValue) Then
                            writer.WriteElementString("dc", "date", Nothing, yearValue.ToString("yyyy-MM-dd"))
                        End If
                    End If
                End If
                Dim displayArtist As String = tags(MetaDataIndex.Artist)
                If displayArtist.Length > 0 Then
                    If filterSet Is Nothing OrElse filterSet.Contains("dc:creator") Then
                        writer.WriteElementString("dc", "creator", Nothing, displayArtist)
                    End If
                    If filterSet Is Nothing OrElse filterSet.Contains("upnp:artist") Then
                        writer.WriteElementString("upnp", "artist", Nothing, displayArtist)
                        Dim artists() As String = tags(MetaDataIndex.ArtistPeople).Split(ChrW(0))
                        For index As Integer = 0 To artists.Length - 1
                            Dim artist As String = artists(index)
                            If artist.Length > 0 Then
                                Dim role As String = "Performer"
                                If artist.Chars(0) < " "c Then
                                    If AscW(artist.Chars(0)) = 4 Then
                                        role = "Remixer"
                                    End If
                                    artist = artist.Substring(1)
                                End If
                                If String.Compare(artist, displayArtist, StringComparison.OrdinalIgnoreCase) <> 0 Then
                                    writer.WriteStartElement("upnp", "artist", Nothing)
                                    writer.WriteAttributeString("role", role)
                                    writer.WriteValue(artist)
                                    writer.WriteEndElement()
                                End If
                            End If
                        Next index
                        Dim composer As String = tags(MetaDataIndex.Composer)
                        If composer.Length > 0 Then
                            writer.WriteStartElement("upnp", "author", Nothing)
                            writer.WriteAttributeString("role", "Composer")
                            writer.WriteValue(composer)
                            writer.WriteEndElement()
                        End If
                        Dim conductor As String = tags(MetaDataIndex.Conductor)
                        If conductor.Length > 0 Then
                            writer.WriteStartElement("upnp", "artist", Nothing)
                            writer.WriteAttributeString("role", "Conductor")
                            writer.WriteValue(conductor)
                            writer.WriteEndElement()
                        End If
                        writer.WriteStartElement("upnp", "artist", Nothing)
                        writer.WriteAttributeString("role", "AlbumArtist")
                        writer.WriteValue(tags(MetaDataIndex.AlbumArtist))
                        writer.WriteEndElement()
                        writer.WriteElementString("upnp", "albumArtist", Nothing, tags(MetaDataIndex.AlbumArtist))
                    End If
                End If
                Dim album As String = tags(MetaDataIndex.Album)
                If album.Length > 0 AndAlso (filterSet Is Nothing OrElse filterSet.Contains("upnp:album")) Then
                    writer.WriteElementString("upnp", "album", Nothing, album)
                End If
                Dim trackNumber As String = tags(MetaDataIndex.TrackNo)
                If trackNumber.Length > 0 AndAlso (filterSet Is Nothing OrElse filterSet.Contains("upnp:originalTrackNumber")) Then
                    writer.WriteElementString("upnp", "originalTrackNumber", Nothing, trackNumber)
                End If
                Dim discNumber As String = tags(MetaDataIndex.DiscNo)
                If discNumber.Length > 0 AndAlso (filterSet Is Nothing OrElse filterSet.Contains("upnp:originalDiscNumber")) Then
                    writer.WriteElementString("upnp", "originalDiscNumber", Nothing, discNumber)
                End If
                Dim discCount As String = tags(MetaDataIndex.DiscCount)
                If discCount.Length > 0 AndAlso (filterSet Is Nothing OrElse filterSet.Contains("upnp:originalDiscCount")) Then
                    writer.WriteElementString("upnp", "originalDiscCount", Nothing, discCount)
                End If
                Dim publisher As String = tags(MetaDataIndex.Publisher)
                If publisher.Length > 0 AndAlso (filterSet Is Nothing OrElse filterSet.Contains("upnp:publisher")) Then
                    writer.WriteElementString("upnp", "publisher", Nothing, publisher)
                End If
                Dim genre As String = tags(MetaDataIndex.Genre)
                If genre.Length > 0 AndAlso (filterSet Is Nothing OrElse filterSet.Contains("upnp:genre")) Then
                    writer.WriteElementString("upnp", "genre", Nothing, genre)
                End If
                Dim rating As Double
                If Double.TryParse(tags(MetaDataIndex.Rating), rating) AndAlso rating >= 0 AndAlso (filterSet Is Nothing OrElse filterSet.Contains("upnp:rating")) Then
                    writer.WriteElementString("upnp", "rating", Nothing, CInt(If(rating = 0, 1, rating * 20)).ToString())
                End If
                Dim dateAdded As String = tags(MetaDataIndex.DateAdded)
                Dim dateAddedTicks As Long
                If dateAdded <> "0" AndAlso Long.TryParse(dateAdded, dateAddedTicks) AndAlso (filterSet Is Nothing OrElse filterSet.Contains("pv:addedTime")) Then
                    Dim dateAddedValue As New DateTime(dateAddedTicks)
                    ' Same family of time-format bug as F28: `hh` is 12-hour clock for DateTime - must be `HH`
                    ' for 24-hour ISO-8601 timestamps. Without this any track added between 13:00 and 23:59
                    ' would render as 01:00-11:59 (e.g. 17:42 → "05:42") on devices that surface the field.
                    writer.WriteElementString("pv", "addedTime", Nothing, dateAddedValue.ToString("yyyy-MM-dd'T'HH':'mm':'ss"))
                End If
                Dim dateLastPlayed As String = tags(MetaDataIndex.DateLastPlayed)
                Dim dateLastPlayedTicks As Long
                If dateLastPlayed <> "0" AndAlso Long.TryParse(dateLastPlayed, dateLastPlayedTicks) AndAlso (filterSet Is Nothing OrElse filterSet.Contains("pv:lastPlayedTime")) Then
                    Dim dateLastPlayedValue As New DateTime(dateLastPlayedTicks)
                    writer.WriteElementString("pv", "lastPlayedTime", Nothing, dateLastPlayedValue.ToString("yyyy-MM-dd'T'HH':'mm':'ss"))
                End If
                Dim playCount As String = tags(MetaDataIndex.PlayCount)
                If playCount <> "0" Then
                    If filterSet Is Nothing OrElse filterSet.Contains("pv:playcount") Then
                        writer.WriteElementString("pv", "playcount", Nothing, playCount)
                    End If
                    If filterSet Is Nothing OrElse filterSet.Contains("upnp:playbackCount") Then
                        writer.WriteElementString("upnp", "playbackCount", Nothing, playCount)
                    End If
                End If
                If (filterSet Is Nothing OrElse filterSet.Contains("upnp:albumArtURI")) AndAlso streamingProfile.PictureSize > 0 Then
                    If mbApiInterface.Library_GetArtworkUrl(url, -2) <> "0" Then
                        For Each size As String In New String() {"JPEG_TN", "JPEG_SM"}
                            If size = "JPEG_TN" OrElse streamingProfile.PictureSize = 160 Then
                                writer.WriteStartElement("upnp", "albumArtURI", Nothing)
                                writer.WriteAttributeString("dlna", "profileID", "urn:schemas-dlna-org:metadata-1-0/", size)
                                writer.WriteValue(String.Format("{0}/thumbnail/{1}.{2}", hostUrl, id, If(size = "JPEG_TN" AndAlso streamingProfile.PictureSize <> 160, streamingProfile.PictureSize.ToString("0000000"), size)))
                                writer.WriteEndElement()
                            End If
                        Next size
                    End If
                End If
                If filterSet Is Nothing OrElse filterSet.Any(Function(a) a.StartsWith("res")) Then
                    Dim duration As TimeSpan
                    Dim durationValue As Long
                    If Long.TryParse(tags(MetaDataIndex.Duration), durationValue) Then
                        duration = New TimeSpan(durationValue)
                    End If
                    If duration.Ticks = 0 AndAlso streamHandle <> 0 Then
                        Dim fileDuration As Double = Bass.GetDecodedDuration(streamHandle)
                        If fileDuration > 0 Then
                            ' some files (eg. aac files might not have a cached duration)
                            duration = New TimeSpan(CLng(fileDuration * TimeSpan.TicksPerSecond))
                        End If
                    End If
                    If forcedCodec = FileCodec.Unknown OrElse (forcedCodec = sourceFileCodec AndAlso Not forceEncode) Then
                        writer.WriteStartElement("res")
                        Dim mime As String = GetMime(sourceFileCodec, 16)
                        writer.WriteAttributeString("protocolInfo", String.Format("http-get:*:{0}:{1}", mime, GetFileFeature(sourceFileCodec, (duration.Ticks <= 0))))
                        If duration.Ticks > 0 Then
                            If filterSet Is Nothing OrElse filterSet.Contains("res@size") Then
                                writer.WriteAttributeString("size", tags(MetaDataIndex.Size))
                            End If
                            If filterSet Is Nothing OrElse filterSet.Contains("res@duration") Then
                                ' F28 - UPnP DIDL spec is `H+:MM:SS[.F+]`; the bare `H:MM:SS` form some
                                ' Marantz devices refused to parse, leaving track duration blank in
                                ' their display. Adding `.fff` makes the format strictly conformant.
                                writer.WriteAttributeString("duration", duration.ToString("h':'mm':'ss'.'fff"))
                            End If
                        End If
                        If filterSet Is Nothing OrElse filterSet.Contains("res@bitrate") Then
                            Dim bitRate As String = tags(MetaDataIndex.Bitrate)
                            Dim bitrateValue As Integer
                            If Integer.TryParse(bitRate, bitrateValue) Then
                                writer.WriteAttributeString("bitrate", ((bitrateValue * 1000) \ 8).ToString())
                            End If
                        End If
                        If filterSet Is Nothing OrElse filterSet.Contains("res@sampleFrequency") Then
                            writer.WriteAttributeString("sampleFrequency", tags(MetaDataIndex.SampleRate))
                        End If
                        If filterSet Is Nothing OrElse filterSet.Contains("res@nrAudioChannels") Then
                            writer.WriteAttributeString("nrAudioChannels", tags(MetaDataIndex.Channels))
                        End If
                        If isWebFile AndAlso Not musicBeePlayToMode Then
                            defaultHttpUrl = url
                        Else
                            defaultHttpUrl = String.Format("{0}/files/{1}.{2}", hostUrl, If(Not musicBeePlayToMode, id, id & "p"), mime.Substring(6))
                        End If
                        writer.WriteValue(defaultHttpUrl)
                        writer.WriteEndElement()
                    End If
                    Dim encodeSampleRate As Integer = If(streamingProfile.TranscodeSampleRate = -1, sampleRate, streamingProfile.TranscodeSampleRate)
                    For Each encoder As AudioEncoder In encodeSettings.Audio.Encoders
                        If (encoder.Codec = forcedCodec AndAlso (forcedCodec <> sourceFileCodec OrElse forceEncode)) OrElse ((forcedCodec = FileCodec.Unknown OrElse forcedCodec = FileCodec.AnyPcm) AndAlso (((encoder.Codec = FileCodec.Pcm OrElse encoder.Codec = FileCodec.Wave) AndAlso Not Settings.BandwidthConstrained))) Then
                            writer.WriteStartElement("res")
                            writer.WriteAttributeString("protocolInfo", String.Format("http-get:*:{0}:{1}", GetMime(encoder.Codec, streamingProfile.TranscodeBitDepth), GetEncodeFeature(encoder.Codec, (duration.Ticks <= 0))))
                            Dim isPcmData As Boolean = (encoder.Codec = FileCodec.Pcm OrElse encoder.Codec = FileCodec.Wave)
                            If duration.Ticks > 0 Then
                                If filterSet Is Nothing OrElse filterSet.Contains("res@duration") Then
                                    ' F28 - see comment above on the source-file path; same fix here for the encoded-stream path.
                                    writer.WriteAttributeString("duration", duration.ToString("h':'mm':'ss'.'fff"))
                                End If
                                If streamHandle <> 0 AndAlso isPcmData AndAlso (filterSet Is Nothing OrElse filterSet.Contains("res@size")) Then
                                    Dim streamSampleRate As Integer
                                    Dim streamChannelCount As Integer
                                    Dim streamCodec As FileCodec
                                    If Bass.TryGetStreamInformation(streamHandle, streamSampleRate, streamChannelCount, streamCodec) Then
                                        Dim fileLength As Long = Bass.GetDecodedLength(streamHandle, duration.Ticks / TimeSpan.TicksPerSecond)
                                        If fileLength > 0 Then
                                            fileLength = (fileLength * channelCount) \ streamChannelCount
                                            fileLength = (fileLength * encodeSampleRate) \ streamSampleRate
                                            If streamingProfile.TranscodeBitDepth <> 24 Then
                                                fileLength \= 2
                                            Else
                                                fileLength = (fileLength * 3) \ 4
                                            End If
                                            If encoder.Codec = FileCodec.Wave Then
                                                fileLength += 44
                                            End If
                                            writer.WriteAttributeString("size", fileLength.ToString())
                                        End If
                                    End If
                                End If
                            End If
                            If filterSet Is Nothing OrElse filterSet.Contains("res@sampleFrequency") Then
                                writer.WriteAttributeString("sampleFrequency", encodeSampleRate.ToString())
                            End If
                            If filterSet Is Nothing OrElse filterSet.Contains("res@bitsPerSample") Then
                                writer.WriteAttributeString("bitsPerSample", If(Not isPcmData, "16", streamingProfile.TranscodeBitDepth.ToString()))
                            End If
                            If filterSet Is Nothing OrElse filterSet.Contains("res@nrAudioChannels") Then
                                writer.WriteAttributeString("nrAudioChannels", If(Not isPcmData, "2", channelCount.ToString()))
                            End If
                            If isPcmData AndAlso (filterSet Is Nothing OrElse filterSet.Contains("res@bitrate")) Then
                                writer.WriteAttributeString("bitrate", ((encodeSampleRate * channelCount * streamingProfile.TranscodeBitDepth) \ 8).ToString())
                            End If
                            Dim httpUrl As String = String.Format("{0}/encode/{1}{2}.{3}", hostUrl, id, streamHandle.ToString(), GetMime(encoder.Codec, streamingProfile.TranscodeBitDepth).Substring(6))
                            If defaultHttpUrl Is Nothing Then
                                defaultHttpUrl = httpUrl
                            End If
                            writer.WriteValue(httpUrl)
                            writer.WriteEndElement()
                        End If
                    Next encoder
                End If
            End If
            writer.WriteEndElement()
            Return defaultHttpUrl
        End Function

        Private Function IsCodecSupported(codec As FileCodec) As Boolean
            If codec = FileCodec.Unknown Then
                Return False
            ElseIf SupportedMimeTypes Is Nothing Then
                Return True
            Else
                Dim mimeTypes() As String = GetMimes(codec)
                For index As Integer = 0 To SupportedMimeTypes.Length - 1
                    For index2 As Integer = 0 To mimeTypes.Length - 1
                        If SupportedMimeTypes(index).StartsWith(mimeTypes(index2), StringComparison.OrdinalIgnoreCase) Then
                            Return True
                        End If
                    Next index2
                Next index
                ' F34 - Fallback for codecs that are near-universally supported by modern renderers
                ' but which some devices fail to advertise in their protocol info.
                Select Case codec
                    Case FileCodec.Mp3, FileCodec.Aac, FileCodec.AacNoContainer, FileCodec.Alac, FileCodec.Flac
                        Return True
                End Select
                Return False
            End If
        End Function

        Public Function GetCodec(extension As String) As FileCodec
            If String.IsNullOrEmpty(extension) Then
                Return FileCodec.Unknown
            Else
                Select Case extension
                    Case ".mp3", ".mpeg", ".mpe"
                        ' F31 - `.mpeg` and `.mpe` are valid (rare) extensions for MPEG-1 Layer 3 audio.
                        ' Without this, files with those extensions returned FileCodec.Unknown and were
                        ' silently rejected - both as library sources and as transcode source candidates.
                        Return FileCodec.Mp3
                    Case ".m4a", ".m4b", ".mp2", ".mp4"
                        Return FileCodec.Aac
                    Case ".aac"
                        Return FileCodec.AacNoContainer
                    Case ".wma"
                        Return FileCodec.Wma
                    Case ".opus"
                        Return FileCodec.Opus
                    Case ".ogg", ".oga"
                        Return FileCodec.Ogg
                    Case ".spx"
                        Return FileCodec.Spx
                    Case ".flac"
                        Return FileCodec.Flac
                    Case ".wv"
                        Return FileCodec.WavPack
                    Case ".tak"
                        Return FileCodec.Tak
                    Case ".mpc", ".mp+", ".mpp"
                        Return FileCodec.Mpc
                    Case ".wav"
                        Return FileCodec.Wave
                    Case ".aiff"
                        Return FileCodec.Aiff
                    Case ".pcm"
                        Return FileCodec.Pcm
                    Case ".ape"
                        ' F22 - Monkey Audio source support.
                        Return FileCodec.Ape
                    Case Else
                        Return FileCodec.Unknown
                End Select
            End If
        End Function

        'Private Function GetExtension(codec As FileCodec) As String
        '    Select Case codec
        '        Case FileCodec.Mp3
        '            Return ".mp3"
        '        Case FileCodec.Aac, FileCodec.Alac
        '            Return ".m4a"
        '        Case FileCodec.AacNoContainer
        '            Return ".aac"
        '        Case FileCodec.Wma
        '            Return ".wma"
        '        Case FileCodec.Ogg
        '            Return ".ogg"
        '        Case FileCodec.Flac
        '            Return ".flac"
        '        Case FileCodec.WavPack
        '            Return ".wv"
        '        Case FileCodec.Wave
        '            Return ".wav"
        '        Case FileCodec.Tak
        '            Return ".tak"
        '        Case FileCodec.Mpc
        '            Return ".mpc"
        '        Case FileCodec.Aiff
        '            Return ".aiff"
        '        Case Else
        '            Return ".pcm"
        '    End Select
        'End Function

        Private Function GetMime(codec As FileCodec, bitDepth As Integer) As String
            Dim mimes() As String = GetMimes(codec)
            If codec = FileCodec.Pcm Then
                mimes = New String() {mimes(If(bitDepth <> 24, 0, 1))}
            End If
            If SupportedMimeTypes Is Nothing Then
                If mimes.Length > 0 Then
                    Return mimes(0)
                End If
                Return ""
            Else
                For index As Integer = 0 To mimes.Length - 1
                    For index2 As Integer = 0 To SupportedMimeTypes.Length - 1
                        If SupportedMimeTypes(index2).StartsWith(mimes(index), StringComparison.OrdinalIgnoreCase) Then
                            Return mimes(index)
                        End If
                    Next index2
                Next index
                Select Case codec
                    Case FileCodec.Wave
                        Return "audio/wav"
                    Case FileCodec.Pcm
                        Return "audio/L" & bitDepth
                    Case FileCodec.Aac, FileCodec.Alac
                        ' F23 - fallback for devices that support AAC/ALAC but don't advertise the mime types.
                        Return "audio/m4a"
                    Case FileCodec.AacNoContainer
                        ' F23 - fallback for raw AAC without container.
                        Return "audio/aac"
                    Case Else
                        Return ""
                End Select
            End If
        End Function

        Private Function GetMimes(codec As FileCodec) As String()
            Select Case codec
                Case FileCodec.Mp3
                    Return New String() {"audio/mpeg", "audio/mp3", "audio/x-mp3"}
                Case FileCodec.Aac, FileCodec.Alac
                    Return New String() {"audio/m4a", "audio/mp4"}
                Case FileCodec.AacNoContainer
                    Return New String() {"audio/aac", "audio/x-aac"}
                Case FileCodec.Wma
                    Return New String() {"audio/wma", "audio/x-ms-wma"}
                Case FileCodec.Ogg
                    Return New String() {"audio/ogg", "application/ogg", "audio/x-ogg"}
                Case FileCodec.Flac
                    Return New String() {"audio/flac", "audio/x-flac"}
                Case FileCodec.WavPack
                    Return New String() {"audio/wavpack", "audio/x-wavpack"}
                Case FileCodec.Wave
                    Return New String() {"audio/wav", "audio/x-wav"}
                Case FileCodec.Tak
                    Return New String() {"audio/tak", "audio/x-tak"}
                Case FileCodec.Mpc
                    Return New String() {"audio/musepack", "audio/x-musepack"}
                Case FileCodec.Aiff
                    Return New String() {"audio/aiff", "audio/x-aiff"}
                Case FileCodec.Pcm
                    Return New String() {"audio/L16", "audio/L24"}
                Case FileCodec.Opus
                    ' F21 - Opus mime support. Native Opus mime first; ogg fallback for renderers that only recognize the container.
                    Return New String() {"audio/opus", "audio/ogg"}
                Case FileCodec.Ape
                    ' F22 - Monkey Audio. No universally-accepted standard mime; offer both common forms.
                    Return New String() {"audio/x-ape", "audio/ape", "audio/x-monkeys-audio"}
                Case Else
                    Return New String() {}
            End Select
        End Function

        Private Function GetDlnaType(codec As FileCodec) As String
            Select Case codec
                Case FileCodec.Mp3
                    Return "DLNA.ORG_PN=MP3;"
                Case FileCodec.Aac, FileCodec.AacNoContainer
                    Return "DLNA.ORG_PN=AAC_ISO;"
                Case FileCodec.Alac
                    Return "DLNA.ORG_PN=ALAC;"
                Case FileCodec.Wma
                    Return "DLNA.ORG_PN=WMABASE;"
                Case FileCodec.Pcm
                    Return "DLNA.ORG_PN=LPCM;"
                Case FileCodec.Wave
                    ' F25 - Wave (PCM-in-RIFF) is conventionally tagged as LPCM by DLNA servers.
                    Return "DLNA.ORG_PN=LPCM;"
                Case FileCodec.Flac
                    ' F26 - Non-standard but widely supported by hi-fi renderers.
                    Return "DLNA.ORG_PN=FLAC;"
                Case Else
                    Return String.Empty
            End Select
        End Function

        Public Function GetFileFeature(url As String, disableSeek As Boolean) As String
            Return GetFileFeature(GetCodec(url), disableSeek)
        End Function

        Private Function GetFileFeature(codec As FileCodec, disableSeek As Boolean) As String
            Return GetDlnaType(codec) & If(disableSeek, "DLNA.ORG_OP=00;", If(DisablePcmTimeSeek, "DLNA.ORG_OP=01;", "DLNA.ORG_OP=11;")) & "DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000"
        End Function

        Public Function GetEncodeFeature(codec As FileCodec, disableSeek As Boolean) As String
            'DLNA.ORG_CI=1;
            ' DLNA.ORG_OP semantics:
            '   00 = no seek         01 = time-based seek only
            '   10 = byte-based only  11 = both byte and time seek
            ' Before F30 every non-PCM encoded stream advertised OP=10 (byte-only). For MP3
            ' specifically that's overly conservative: MusicBee's transcoder produces constant-
            ' bitrate MP3 (HighQuality preset), so byte ↔ time mapping is linear and the device
            ' can do time-seek by converting to byte-seek itself. Advertising OP=11 unlocks the
            ' device's native time-seek UI for transcoded MP3 streams.
            Dim opCode As String
            If disableSeek Then
                opCode = "DLNA.ORG_OP=00;"
            ElseIf codec = FileCodec.Pcm OrElse codec = FileCodec.Wave Then
                opCode = If(DisablePcmTimeSeek, "DLNA.ORG_OP=01;", "DLNA.ORG_OP=11;")
            ElseIf codec = FileCodec.Mp3 Then
                ' F30 - CBR transcoded MP3 supports both byte and time seek.
                opCode = "DLNA.ORG_OP=11;"
            Else
                opCode = "DLNA.ORG_OP=10;"
            End If
            Return GetDlnaType(codec) & opCode & "DLNA.ORG_CI=1;DLNA.ORG_FLAGS=01700000000000000000000000000000"
        End Function

        Public Function GetContinuousStreamFeature(codec As FileCodec) As String
            Return GetDlnaType(codec) & "DLNA.ORG_OP=00;DLNA.ORG_CI=1;DLNA.ORG_FLAGS=01700000000000000000000000000000"
        End Function

        Private Shared Function GetFileId(url As String) As String
            Return (Hex(StringComparer.OrdinalIgnoreCase.GetHashCode(url)) & Hex(StringComparer.OrdinalIgnoreCase.GetHashCode(IO.Path.GetFileName(url)))).PadLeft(16, "0"c)
        End Function

        Private Shared Function GetBucket(name As String) As Char
            If name.Length = 0 Then
                Return "#"c
            ElseIf Not Char.IsLetter(name.Chars(0)) Then
                Return "#"c
            Else
                Return Char.ToUpper(name.Chars(0))
            End If
        End Function

        Private Shared Function GetNameBucket(name As String) As Char
startRemovePrefix:
            If ignoreNameChars IsNot Nothing Then
                Do While name.Length > 0 AndAlso ignoreNameChars.IndexOf(name.Chars(0)) <> -1
                    name = name.Substring(1)
                Loop
            End If
            For index As Integer = 0 To ignoreNamePrefixes.Length - 1
                If name.StartsWith(ignoreNamePrefixes(index), StringComparison.OrdinalIgnoreCase) Then
                    name = name.Substring(ignoreNamePrefixes(index).Length)
                    GoTo startRemovePrefix
                End If
            Next index
            If name.Length = 0 Then
                Return "#"c
            ElseIf Not Char.IsLetter(name.Chars(0)) Then
                Return "#"c
            Else
                Return Char.ToUpper(name.Chars(0))
            End If
        End Function

        Friend NotInheritable Class AlbumFileComparer
            Inherits Comparer(Of String())
            Public Overrides Function Compare(tags1() As String, tags2() As String) As Integer
                Dim result As Integer
                result = String.Compare(tags1(MetaDataIndex.Album), tags2(MetaDataIndex.Album), StringComparison.OrdinalIgnoreCase)
                If result <> 0 Then
                    Return result
                Else
                    result = String.Compare(tags1(MetaDataIndex.AlbumArtist), tags2(MetaDataIndex.AlbumArtist), StringComparison.OrdinalIgnoreCase)
                    If result <> 0 Then
                        Return result
                    Else
                        Dim discNo1 As Integer
                        If Not Integer.TryParse(tags1(MetaDataIndex.DiscNo), discNo1) Then
                            discNo1 = -1
                        End If
                        If discNo1 < 0 Then
                            result = String.Compare(tags1(MetaDataIndex.DiscNo), tags2(MetaDataIndex.DiscNo), StringComparison.Ordinal)
                            If result <> 0 Then
                                Return result
                            End If
                        End If
                        Dim discNo2 As Integer
                        If Not Integer.TryParse(tags2(MetaDataIndex.DiscNo), discNo2) Then
                            discNo2 = -1
                        End If
                        If discNo2 < 0 AndAlso discNo1 >= 0 Then
                            result = String.Compare(tags1(MetaDataIndex.DiscNo), tags2(MetaDataIndex.DiscNo), StringComparison.OrdinalIgnoreCase)
                            If result <> 0 Then
                                Return result
                            End If
                        End If
                        If discNo1 <> discNo2 Then
                            Return discNo1 - discNo2
                        End If
                        Dim trackNo1 As Integer
                        Dim trackNo2 As Integer
                        If Not Integer.TryParse(tags1(MetaDataIndex.TrackNo), trackNo1) Then
                            trackNo1 = -1
                        End If
                        If Not Integer.TryParse(tags2(MetaDataIndex.TrackNo), trackNo2) Then
                            trackNo2 = -1
                        End If
                        If trackNo1 > 0 AndAlso trackNo2 > 0 Then
                            Return trackNo1 - trackNo2
                        End If
                        If trackNo1 = trackNo2 Then
                            If trackNo1 <> 0 Then
                                Return 0
                            Else
                                Return String.Compare(tags1(MetaDataIndex.TrackNo), tags2(MetaDataIndex.TrackNo), StringComparison.Ordinal)
                            End If
                        ElseIf trackNo1 = 0 Then
                            Return -1
                        ElseIf trackNo2 = 0 Then
                            Return 1
                        Else
                            Return If(trackNo1 < trackNo2, -1, 1)
                        End If
                    End If
                End If
            End Function
        End Class  ' AlbumFileComparer

        Friend NotInheritable Class TrackNameFileComparer
            Inherits Comparer(Of String())
            Public Overrides Function Compare(tags1() As String, tags2() As String) As Integer
                Return String.Compare(tags1(MetaDataIndex.Title), tags2(MetaDataIndex.Title), StringComparison.OrdinalIgnoreCase)
            End Function
        End Class  ' TrackNameFileComparer

        Friend NotInheritable Class FolderNameComparer
            Inherits Comparer(Of FolderNode)
            Public Overrides Function Compare(node1 As FolderNode, node2 As FolderNode) As Integer
                Return String.Compare(node1.Name, node2.Name, StringComparison.OrdinalIgnoreCase)
            End Function
        End Class  ' FolderNameComparer

        Friend NotInheritable Class FolderPrefixedNameComparer
            Inherits Comparer(Of FolderNode)
            Public Overrides Function Compare(node1 As FolderNode, node2 As FolderNode) As Integer
                Dim name1Changed As Boolean = False
                Dim name2Changed As Boolean = False
startRemovePrefix:
                If ignoreNameChars IsNot Nothing Then
                    Do While node1.Name.Length > 0 AndAlso ignoreNameChars.IndexOf(node1.Name.Chars(0)) <> -1
                        name1Changed = True
                        node1.Name = node1.Name.Substring(1)
                    Loop
                    Do While node2.Name.Length > 0 AndAlso ignoreNameChars.IndexOf(node2.Name.Chars(0)) <> -1
                        name2Changed = True
                        node2.Name = node2.Name.Substring(1)
                    Loop
                End If
                For index As Integer = 0 To ignoreNamePrefixes.Length - 1
                    If node1.Name.StartsWith(ignoreNamePrefixes(index), StringComparison.OrdinalIgnoreCase) Then
                        If String.Compare(node1.Name, "The The", StringComparison.Ordinal) = 0 Then
                            Exit For
                        End If
                        name1Changed = True
                        node1.Name = node1.Name.Substring(ignoreNamePrefixes(index).Length)
                        GoTo startRemovePrefix
                    End If
                    If node2.Name.StartsWith(ignoreNamePrefixes(index), StringComparison.OrdinalIgnoreCase) Then
                        If String.Compare(node2.Name, "The The", StringComparison.Ordinal) = 0 Then
                            Exit For
                        End If
                        name2Changed = True
                        node2.Name = node2.Name.Substring(ignoreNamePrefixes(index).Length)
                        GoTo startRemovePrefix
                    End If
                Next index
                Dim result As Integer = String.Compare(node1.Name, node2.Name, StringComparison.OrdinalIgnoreCase)
                If result <> 0 OrElse Not (name1Changed Xor name2Changed) Then
                    Return result
                Else
                    Return If(name1Changed, 1, -1)
                End If
            End Function
        End Class  ' FolderPrefixedNameComparer

        ' Internal alias for Plugin.MetaDataType - mixes in FilePropertyType-style values
        ' (Url=2, FileSize=7 etc.) which the plugin can request via Library_GetFileTags by passing
        ' negative values. New entries below the original block extend to cover the user's
        ' grouping-field set (Sort variants, Mood/Grouping/etc., Custom1-16, Virtual1-25).
        Friend Enum MetaDataType As Short
            Id = 1
            Url = 2
            FileKind = 4
            FileSize = 7
            Channels = 8
            SampleRate = 9
            Bitrate = 10
            DateModified = 11
            DateAdded = 12
            DateLastPlayed = 13
            PlayCount = 14
            DiscNo = 52
            DiscCount = 54
            Duration = 16
            Category = 42
            TrackTitle = 65
            Name = 65
            Album = 30
            Artist = 32
            ArtistPeople = 33
            AlbumArtist = 31
            Composer = 43
            Conductor = 45
            Genre = 59
            Publisher = 73
            Rating = 75
            TrackNo = 86
            TrackCount = 87
            Year = 88
            YearOnly = 35
            ReplayGainTrack = 94
            ReplayGainAlbum = 95
            ' Sort variants
            SortAlbumArtist = 165
            SortArtist = 166
            SortComposer = 167
            SortAlbum = 164
            ' Additional standard grouping fields
            Mood = 64
            Grouping = 61
            Work = 168
            Language = 173
            Occasion = 66
            Origin = 67
            OriginalArtist = 174
            OriginalYear = 175
            ' Custom slots 1-16
            Custom1 = 46
            Custom2 = 47
            Custom3 = 48
            Custom4 = 49
            Custom5 = 50
            Custom6 = 96
            Custom7 = 97
            Custom8 = 98
            Custom9 = 99
            Custom10 = 128
            Custom11 = 129
            Custom12 = 130
            Custom13 = 131
            Custom14 = 132
            Custom15 = 133
            Custom16 = 134
            ' Virtual slots 1-25 (formula-evaluated by MusicBee)
            Virtual1 = 109
            Virtual2 = 110
            Virtual3 = 111
            Virtual4 = 112
            Virtual5 = 113
            Virtual6 = 122
            Virtual7 = 123
            Virtual8 = 124
            Virtual9 = 125
            Virtual10 = 135
            Virtual11 = 136
            Virtual12 = 137
            Virtual13 = 138
            Virtual14 = 139
            Virtual15 = 140
            Virtual16 = 141
            Virtual17 = 149
            Virtual18 = 150
            Virtual19 = 151
            Virtual20 = 152
            Virtual21 = 153
            Virtual22 = 154
            Virtual23 = 155
            Virtual24 = 156
            Virtual25 = 157
        End Enum  ' MetaDataType

        ' Positional indices into the tags() array that Library_GetFileTags fills. Order MUST
        ' match queryFields exactly. The first ~27 positions are the legacy set; positions 27+
        ' carry the extra grouping-field set added for the Fields tab (Sort variants, additional
        ' standard fields, Custom 1-16, Virtual 1-25).
        Friend Enum MetaDataIndex
            None = -1
            Url = 0
            Category = 1
            Artist = 2
            ArtistPeople = 3
            AlbumArtist = 4
            Composer = 5
            Conductor = 6
            Title = 7
            Album = 8
            TrackNo = 9
            DiscNo = 10
            DiscCount = 11
            Year = 12
            Genre = 13
            Publisher = 14
            Rating = 15
            Duration = 16
            Size = 17
            Bitrate = 18
            SampleRate = 19
            Channels = 20
            DateAdded = 21
            PlayCount = 22
            DateLastPlayed = 23
            ReplayGainTrack = 24
            AlbumArtistAndAlbum = 25
            AlbumArtistSort = 26
            ' Extension - extra fields for the Views/Fields curation.
            SortArtist = 27
            SortComposer = 28
            SortAlbum = 29
            Mood = 30
            Grouping = 31
            Work = 32
            Language = 33
            Occasion = 34
            Origin = 35
            OriginalArtist = 36
            OriginalYear = 37
            Custom1 = 38
            Custom2 = 39
            Custom3 = 40
            Custom4 = 41
            Custom5 = 42
            Custom6 = 43
            Custom7 = 44
            Custom8 = 45
            Custom9 = 46
            Custom10 = 47
            Custom11 = 48
            Custom12 = 49
            Custom13 = 50
            Custom14 = 51
            Custom15 = 52
            Custom16 = 53
            Virtual1 = 54
            Virtual2 = 55
            Virtual3 = 56
            Virtual4 = 57
            Virtual5 = 58
            Virtual6 = 59
            Virtual7 = 60
            Virtual8 = 61
            Virtual9 = 62
            Virtual10 = 63
            Virtual11 = 64
            Virtual12 = 65
            Virtual13 = 66
            Virtual14 = 67
            Virtual15 = 68
            Virtual16 = 69
            Virtual17 = 70
            Virtual18 = 71
            Virtual19 = 72
            Virtual20 = 73
            Virtual21 = 74
            Virtual22 = 75
            Virtual23 = 76
            Virtual24 = 77
            Virtual25 = 78
            ' --- Synthetic / Extra slots ---
            ' Not backed by Library_GetFileTags. Populated explicitly by loaders that have
            ' data outside MusicBee's tag schema (currently: podcast subscription folder).
            ' Tag arrays are padded to TagArraySize after every Library_GetFileTags so the
            ' index is always valid even on regular tracks (empty string).
            ExtraField1 = 79
        End Enum  ' MetaDataIndex

        ' Tag-array sizing constant: max MetaDataIndex value + 1. Every tags() array MUST be
        ' at least this long so synthetic slots like ExtraField1 are always addressable, even
        ' on rows where MB's API only fills queryFields-length slots.
        Friend Const TagArraySize As Integer = 80

        Friend Enum MediaType
            Image
            Audio
            Video
            Playlist
            Other
        End Enum  ' MediaType

        Friend Class TemplateNode
            Public Path As String
            Public ParentId As String
            Public Id As String
            Public Name As String
            Public ContainerClass As String
            Public IncludeAllTracks As Boolean = False
            Public Fields() As MetaDataIndex
            Public Category As ContainerCategory = ContainerCategory.Music
            Public ChildNodes() As TemplateNode
            Public Sub New(path As String, parentId As String, id As String, name As String, containerClass As String, fields() As MetaDataIndex)
                Me.Path = path
                Me.ParentId = parentId
                Me.Id = id
                Me.Name = name
                Me.ContainerClass = containerClass
                Me.Fields = fields
            End Sub
        End Class  ' TemplateNode

        Friend Class PlaylistFolderNode
            Public Path As String
            Public Name As String
            Public Folders As New List(Of PlaylistFolderNode)
            Public Sub New()
            End Sub
            Public Sub New(name As String)
                Me.Name = name
            End Sub
        End Class  ' PlaylistFolderNode

        ' FolderNode - promoted from Structure to NotInheritable Class to eliminate
        ' the value-copy hazards that made the browse-tree assembly brittle. With a
        ' Structure, adding a FolderNode to a List copied the value, so any later
        ' mutation didn't propagate to the listed copy - that footgun broke the
        ' lazy-load refactor and any future state-mutation pattern. As a Class,
        ' every reference points to the same instance.
        ' Migration notes for callers:
        '   - `Dim x As FolderNode` now declares a Nothing reference (used to be a
        '     default struct). Anywhere we previously relied on the implicit empty
        '     struct, we explicitly New it before use.
        '   - ByRef on a class still updates the variable to point at a different
        '     object - functionally close to ByRef on a struct.
        '   - Indexed array assignment (`arr(i).Folders = X`) still mutates in place
        '     for both forms, because indexing returns a reference.
        Friend NotInheritable Class FolderNode
            Public Path As String
            Public Name As String
            Public Folders() As FolderNode
            Public ChildFiles As List(Of String())
            Public IsBucket As Boolean
            Public Sub New()
            End Sub
            Public Sub New(name As String)
                Me.Name = name
            End Sub
            Public Sub New(name As String, files As List(Of String()))
                Me.Name = name
                Me.ChildFiles = files
            End Sub
            Public Sub New(name As String, folders() As FolderNode, files As List(Of String()))
                Me.Name = name
                Me.Folders = folders
                Me.ChildFiles = files
            End Sub
        End Class  ' FolderNode

        Friend Enum ContainerCategory
            Music = 0
            Audiobook = 1
            Inbox = 2
            Radio = 3
            Podcast = 4
            Playlist = 5
        End Enum  ' ContainerCategory

        Friend Class MediaSettings
            Public ReadOnly Audio As MediaSettings

            Public Sub New()
                Audio = New MediaSettings(New AudioEncoder(FileCodec.Pcm), New AudioEncoder(FileCodec.Wave), New AudioEncoder(FileCodec.Mp3), New AudioEncoder(FileCodec.Aac), New AudioEncoder(FileCodec.Ogg))
            End Sub

            Public Class MediaSettings
                Public ReadOnly Encoders As ReadOnlyCollection(Of Encoder)

                Public Sub New(ParamArray encoder() As Encoder)
                    Me.Encoders = New ReadOnlyCollection(Of Encoder)(encoder)
                End Sub
            End Class  ' MediaSettingsImage
        End Class  ' MediaSettings
    End Class  ' ItemManager
End Class