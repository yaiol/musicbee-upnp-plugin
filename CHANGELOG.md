# Changelog

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
