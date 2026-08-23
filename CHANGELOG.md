# Changelog

## 2.0.9 — 2026-08-23

- The What's new and Download links in the update notice open in MusicBee's own language

## 2.0.8 — 2026-08-22

- Server and renderer device descriptions now identify the plugin itself: manufacturer "yaiol", the plugin's own site as manufacturerURL, and a model description naming all three roles — control apps previously introduced it as made by the original author, pointing at MusicBee's website
- Model name and model number in the device description are read from the assembly; the number was a hardcoded "1.0" through every release up to 2.0.7 and now reports the real plugin version
- MusicBee → Preferences → Plugins lists "yaiol" as the author
- Assembly title, company and copyright updated — the copyright carries both claims (© Steven Mayall 2014, © yaiol 2026)
- Device Profiles tab realigned: labels and fields share one left edge, vertical spacing tightened, and the sample-rate "to" label and combo moved clear of the "from" combo they were sitting on top of
- Update check identifies itself with the app id and version instead of a fixed string
- README credits state what the fork rewrote and what remains the original author's code
- Trim over-long explanatory comments in MusicBeeUpnp.vb, AssemblyInfo.vb and UpnpDevice.vb, and restore the stock AssemblyVersion example that had been edited to a real-looking version

## 2.0.7 - 2026-08-08
- A larger track sent from a phone now keeps its title and stays scrubbable. The plugin waits for its local copy while that copy is still arriving, however long it takes, instead of deciding in advance that a file was too big to be worth waiting for. A 65 MB track was refused on that basis and then finished downloading a second later — well within the wait it never used — so it played over the network with a web address for a title and a position slider that did nothing
- The wait now follows the download rather than a stopwatch: while bytes keep arriving it keeps waiting, and it gives up only when the transfer actually stalls. A fixed delay could not tell a slow transfer from a stuck one, so whatever value it held was wrong for somebody — too short on a big file, needlessly long on a dead connection. Stalling is now detected in about a second and a half, faster than the old fixed wait, while a healthy transfer is allowed to finish
- Remove the download size ceiling entirely. It was an assumption about network speed, which the plugin has no way to know
- Fix a latent fault where a download finishing after its slot had been reused could mark the wrong track as ready

## 2.0.6 - 2026-08-03
- An album sent from a phone now plays without a gap between tracks. The plugin accepts the "here is what comes next" instruction the standard defines for exactly this, fetches that track while the current one is still playing, and hands it to MusicBee's own player to cross the boundary. Until now that instruction was not accepted at all, so a controlling app had nowhere to put the upcoming track and resorted to announcing it as if it were the current one — which is also what caused the album problem fixed in the previous release
- Internet radio sent to MusicBee is no longer downloaded. The plugin now reads what the controlling app says the track *is* — the standard distinguishes a music file from a live broadcast — instead of guessing from how large the download has grown
- Long high-resolution tracks are no longer refused a local copy. Any download above 300 MB used to be abandoned as a runaway stream, which caught genuine DSD and 24-bit material — a 20-minute high-resolution movement or a 10-minute DSD track both exceed it — and left them unable to be moved through. With live broadcasts now identified properly there is no need for a size limit, and the remaining one sits far above any real track
- The controlling app is now told which track is queued to play next, rather than being told there isn't one
- Renderer logging now records what the controlling app says a track is, so unexpected behaviour can be traced from the log

## 2.0.5 - 2026-08-03
- The volume on your phone and the volume in MusicBee now mean the same thing. The plugin never told controlling apps what its maximum volume was, so each one had to guess: Symfonium settled on 69, which made its 100% reach only 69% in MusicBee while MusicBee's own 100% read back as 144% on the phone. The renderer now declares the 0–100 range the standard asks for, so both ends agree and the phone's volume buttons can reach the top
- Casting a whole album now keeps working past the first track. A controlling app announces the next track a fraction of a second after the current one, and that announcement was discarding the copy being fetched for the track about to play — so most tracks fell back to playing over the network, losing their title and the ability to jump through them. Copies for several tracks are now kept side by side, so an announcement can no longer cancel the one in use
- Re-announcing a track that is already downloaded no longer fetches it a second time
- Renderer logging now records the track description a controller sends, so questions about what a phone does and doesn't tell us can be answered from a real cast
- Correct a log message that claimed a wait had timed out when it had in fact given up immediately

## 2.0.4 - 2026-08-02
- Jumping to a different point in a track sent from your phone or another server now works; MusicBee refuses to reposition anything it fetches over the network, so the plugin quietly downloads a copy of the track while it plays and moves playback onto that copy, which behaves like any other file on your disk
- A track sent by an app that doesn't give its files an ordinary extension now shows its real title and length from the first note, instead of appearing as a raw web address
- A jump the renderer genuinely cannot carry out is now refused with a proper error, so the controlling app says so instead of the position slider silently sliding back
- Play-to devices that present themselves as one combined unit — a Marantz or Denon streamer, where the player sits inside a manufacturer wrapper alongside a media server — are now read correctly; the plugin was picking up the media server's connection details by mistake and so never checked which formats the streamer could actually play
- A device's model description is now taken into account when matching it to a device profile; it was read from the wrong place and silently thrown away
- Renderer logging now covers seeking, playback start and position polling, so unexpected behaviour with a remote source can be diagnosed from the log
- Downloaded copies live in a temporary folder named after the plugin and are cleared away at startup

## 2.0.3 - 2026-08-02
- Casting a track that lives somewhere else — a file on your phone, a NAS, another media server — now plays in MusicBee; the renderer previously accepted only tracks from MusicBee's own library and silently dropped everything else, after which MusicBee resumed an unrelated leftover track and reported "the source file for the track could not be found"
- Title and duration for such a track are taken from the metadata the controlling app sends, so the progress bar and track info are right even though MusicBee has never seen the file
- A track the renderer genuinely cannot play is now refused outright, so the phone reports the failure itself instead of MusicBee raising an error about a different track entirely
- Sending a new track to MusicBee while it sits paused now starts that track, instead of resuming the previous one and discarding the new one

## 2.0.2 - 2026-08-01
- Searching by artist now returns that artist's tracks instead of the entire library — an artist query previously had its predicate thrown away and was answered as "give me everything", so a search that should match a few hundred tracks returned the whole collection
- Artist search matches both Artist and AlbumArtist, so a compilation's tracks are found whether the performer is on the track or the album
- Search results are now paged properly — the server serves the page the client actually asked for instead of re-sending the first results forever, so scrolling through a long result list works
- Search is much faster: the query runs once for the whole result list instead of once per page, and only the visible page's tags are loaded
- New "Random plays from:" setting (Library ▸ General Options) — pick a MusicBee filter and a control point's Random Tracks / Random Albums draws from that filter instead of the whole library; hidden filters are offered too, and an explicit folder scope from the client still wins
- Random Tracks / Random Albums launched from inside a filter folder now stays inside that filter instead of falling back to the whole library
- Cached search results are dropped when the library is refreshed, so a search no longer serves stale hits after tags change
- Help, GitHub and update-check buttons now open real pages — all three used a shortened app slug that 404'd silently
- Section headers in the settings dialog follow MusicBee's UI font instead of showing a foreign design-time typeface

## 2.0.1 - 2026-07-26
- Podcast episodes now advertise their real duration and file size in the UPnP metadata, so renderers (e.g. BubbleUPnP) no longer re-probe the whole stream on every play
- Podcasts now appear under artist-based browse paths — the show name fills the Artist/AlbumArtist slots instead of leaving them empty, which previously dead-ended those groupings to an empty level
- Direct playback of a track that was never browsed (e.g. BubbleUPnP "Recently Played" after a restart) now force-loads the bounded endpoints and resolves the file instead of failing with "Bad id"
- BubbleUPnP "Random Tracks" and "Random Albums" now return results (paged from the lazy query path) instead of an empty list
- Drilling into an album grouped on an empty tag value (e.g. an untagged year in the inbox) now lists its tracks instead of showing nothing

## 2.0.0 - 2026-07-22

First release of the open-source yaiol fork of the MusicBee UPnP plugin. Every point below carries its full What / Why in the release notes (`pub/live/site/en/releases.md`).

### New in this fork

- N01 - MusicBee as a play-to renderer
- N02 - 5.1 FLAC not auto-downmixed
- N03 - Lazy (on-demand) browse tree
- N04 - Self-healing HTTP port binding
- N05 - SSDP announcements over the multicast group (VPN / point-to-point)
- N06 - Filter-based library exposure
- N07 - `SortAlbumArtist` field wiring
- N08 - Multi-value AlbumArtist handling
- N09 - Album-container artwork (`upnp:albumArtURI`)
- N10 - Track ordering inside filter albums
- N11 - Playlist folder tree fix
- N12 - XML-illegal control character sanitization
- N13 - Radio listing deterministic across paginated Browse
- N14 - UPnP Search album-class returns album containers
- N15 - Working, scope-aware UPnP search with click-through
- N16 - UPnP cache invalidation (`SystemUpdateID`)
- N17 - Podcast subscription artwork
- N18 - Hierarchical (delimited) tag browsing
- N19 - Single root path labelled by its grouping field
- N20 - Merge browse paths that share a first field
- N21 - Category-typed browse paths (Standard / Radio / Podcast)
- N22 - Group podcasts by publish year
- N23 - Collapse single-result grouping levels
- N24 - Year grouping/search against MusicBee's date field
- N25 - Separate "Year" and "Year (yyyy)" group-by fields
- N26 - Sectioned settings dialog
- N27 - Cancel discards path/template edits
- N28 - Literal ampersands in the field-picker menu
- N29 - Stable, untranslated settings window title
- N30 - Multi-language UI (translations pending)
- N31 - Help link opens in the full interface language
- N32 - Pinned filters and playlists grouped by kind at the root

### Fixes and improvements to the original plugin

- F01 - Updated default DLNA device profiles
- F02 - Control devices that advertise `MediaRenderer:3`
- F03 - "Force native stream" per-profile option (default ON)
- F04 - "Force transcoding" per-profile
- F05 - "Force little-endian PCM" per-profile
- F06 - "Do not use Raw PCM" per-profile
- F07 - "Content length" per-profile
- F08 - "Do not clear NextURI" per-profile
- F09 - FLAC as transcode output format
- F10 - SetNextAVTransportURI / NextURI core
- F11 - "Disable NextURI support" per-profile
- F12 - NextURI lifecycle on the now-playing-list
- F13 - NextURI failure backoff
- F14 - Repeat-mode + NextURI integration
- F15 - Track-transition detection state machine
- F16 - Pop-on-gapless-transition fix
- F17 - Progress bar resync after seek
- F18 - Continuous-stream / NextURI interlock
- F19 - Blank NextURI errors ignored
- F20 - MP3 mime → `audio/mpeg`
- F21 - Mime type order: non-`x-` variant first
- F22 - Opus mime type support
- F23 - Monkey Audio (APE) source file support
- F24 - AAC / ALAC mime fallback
- F25 - DLNA type flag for native + encoded WAV streams
- F26 - DLNA header for FLAC files
- F27 - Bitrate calculation fix in metadata
- F28 - Metadata time format fix (Marantz)
- F29 - Encoded MP3 seek support (CBR)
- F30 - `.mpeg` file extension handled
- F31 - Radio streams auto-use continuous mode
- F32 - Codec-advertisement fallback
- F33 - Progress-bar sync improvement
- F34 - Progress bar jitter after track-change
- F35 - "Force transcoding" bug
- F36 - Renderer-closed exception
- F37 - Long-track seek triggering false transition
- F38 - Improved seek handling for crash-prone codecs
- F39 - "Add" button selects the new profile
- F40 - Bigger max-connections + warning log
- F41 - Log "encoding due to ReplayGain/DSP"
- F42 - Log "renderer doesn't support source codec"
- F43 - SetNextAVTransport log shows source URL
- F44 - Better mime-type error logging
- F45 - Better metadata error logging
- F46 - Automatic mode advertises only on real-network adapters
