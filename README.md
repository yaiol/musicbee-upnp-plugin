# MusicBee UPnP — yaiol fork

Open-source fork of Steven Mayall's original MusicBee UPnP plugin. Adds richer library navigation (filter-based root containers, AlbumArtist-sort grouping, album artwork in DIDL responses) and ports forward the most useful playback fixes from the closed-source [UPnP 2025 fork](https://getmusicbee.com/addons/plugins/534/upnp-2025/).

This README explains what every option in the settings dialog actually does. UPnP/DLNA terminology is largely the original authors' jargon; the goal here is to translate it into plain English so you know what to tick.

---

## 1. What this plugin does

UPnP/DLNA is a network protocol that lets devices on your local network share and play media without any cloud or login. This plugin gives MusicBee three roles, each switched on independently on the **General** tab:

### Role A — "Library server"

Your phone, smart TV, hi-fi streamer or speaker can browse MusicBee's music library over the network and play tracks from it. MusicBee acts like a Spotify-on-LAN: you point a UPnP app (BubbleUPnP on Android, Hi-Fi Cast, mconnect, Symfonium with Subsonic — *not* with UPnP, etc.) at MusicBee and it sees your library.

This is the **incoming** direction — clients pull from MusicBee.

### Role B — "Player / controller"

You can pick a UPnP device on your network as MusicBee's output. Press Play in MusicBee, sound comes out of your speaker / streamer / TV.

This is the **outgoing** direction — MusicBee pushes to a renderer.

### Role C — "Renderer" (MusicBee *is* the player)

The reverse of Role A: a controller app on your phone (BubbleUPnP and friends) picks your desktop MusicBee as the thing that *plays*, then drives it — play, pause, skip, seek, volume. Your phone becomes a remote for the music already on your PC.

This role is **off by default**, because switching it on lets anything on your network start playback on your PC. Give it a name (the **renderer name** field) so you can tell machines apart in your phone's play-to list.

You can use any combination of the three. The settings dialog only shows the tabs the roles you enabled actually need.

---

## 2. Words that come up a lot

| Word | What it actually means |
|---|---|
| **Renderer** | A device that plays audio sent over UPnP. Your speaker, streamer, TV, phone-as-speaker. |
| **Control point** | The thing that tells a renderer what to play. Could be a remote-control app on your phone, or MusicBee itself. |
| **Server** | The thing that holds the music and serves files. That's MusicBee here. |
| **Codec** | The audio format: MP3, FLAC, AAC, etc. |
| **Transcode** | Convert one codec to another on the fly. Often needed if the renderer can't play the source format. |
| **Native stream** | Send the file's original bytes without converting. Fast, no quality loss. |
| **Profile** | A set of rules that tells the plugin what a particular device can or can't handle. Matched by user-agent. |
| **DIDL / DIDL-Lite** | The XML format UPnP uses to describe tracks, albums, artists. |
| **DLNA flags** | Bits in the HTTP response telling the renderer what kind of stream it's getting (live vs file, seekable vs not, etc.). |

---

## 3. The settings dialog — section by section

### General

| Option | What it does | When to change |
|---|---|---|
| **language** | Picks the plugin's own UI language (22 available). "Auto" follows MusicBee. | Only change if MusicBee is in one language but you want the plugin in another. |
| **UPnP Server: enable UPnP devices to browse and play from the MusicBee library** | Role A — share your library. **On** by default. | Off if you never browse MusicBee from another device. |
| **UPnP Control Point: add network UPnP/DLNA renderers as MusicBee output devices** | Role B — play *out* to a speaker/streamer. **On** by default. | Off if you don't push from MusicBee to anything. |
| **UPnP Renderer: enable other apps to play to MusicBee** | Role C — let a phone play *to* MusicBee. **Off** by default; needs a restart to take effect. | On if you want your phone to drive playback on this PC. |
| **renderer name** | The name this PC shows up as in your phone's play-to list. Takes effect immediately. | When several PCs run MusicBee and you need to tell them apart. |
| **server name** | The name your other devices see when they discover MusicBee on the network. | Whatever you want. Default is fine. |
| **IP address** | Which network interface MusicBee binds the server to. "Automatic" picks all interfaces. | Multi-network PCs (Wi-Fi + Ethernet + VPN): pick the one your devices are on. |
| **port** | TCP port for the HTTP server. 9779 by default. If that port is already taken, the plugin scans upward for a free one automatically. | Only if another app is already using 9779, or your firewall demands a specific port. |

### Playback (outgoing — MusicBee → renderer)

> The on/off switch for this role lives on the **General** tab (*UPnP Control Point*); this tab holds how it streams.

| Option | What it does | When to change |
|---|---|---|
| **output as a continuous stream** | Sends one infinite audio stream instead of one file per track. Eliminates inter-track gaps. **Tradeoff**: the renderer shows the FIRST track's metadata for the entire session, "next track" buttons stop working, native streaming is disabled (everything must be transcoded). | Last-resort gapless workaround. Better to use a renderer that supports `SetNextAVTransportURI` (NextURI). |
| **network is bandwidth constrained** | When transcoding kicks in, use the device profile's *lossy* output (MP3/AAC/Ogg) regardless. | On if you stream over a slow Wi-Fi link. |
| **force native stream for radio** | Hand radio stations to the device untouched, bypassing the transcoder. **On** by default. | Off only if a device can't play a station's native format. |

### Library (incoming — UPnP clients → MusicBee)

| Option | What it does | When to change |
|---|---|---|
| **enable UPnP devices to browse and play from the MusicBee library** | Turns the server role on/off. | Off if you never browse MusicBee from another device. |
| **bucket tree nodes by the first letter when more than N items** | If you have 10,000 artists, browsing one giant alphabetical list is painful. Bucketing groups them under A, B, C, … letters so you tap a letter first. N = the threshold to trigger bucketing. | Decrease N if buckets aren't appearing where you want them; increase if you'd rather scroll a flat list. |
| **update play statistics in MusicBee** | Tracks played via UPnP count towards MusicBee's play counts and last-played timestamps. | Off if you don't want UPnP plays muddying your stats. |
| **use the equaliser and DSP effects active in MusicBee** | Apply MusicBee's EQ / DSP plugins to outgoing audio. **Forces transcoding** — quality and CPU cost. **Silently ignored when the device's profile has "force native stream" ticked** (see precedence below). | On if you tune playback in MusicBee and want UPnP to sound the same — AND turn off force-native-stream for that profile. |
| **level the playback volume using the replay gain mode active in MusicBee** | Apply ReplayGain. **Forces transcoding**. **Silently ignored when the device's profile has "force native stream" ticked**. | On if you want even loudness across albums — AND turn off force-native-stream for that profile. |

### Device Profiles (per-device rules)

This is the densest part of the dialog. Each profile tells the plugin "for devices matching this user-agent, do these things".

#### Picking which profile applies

The plugin looks at the `User-Agent` header sent by the device. If it matches a profile's "applies when user-agent contains" field, that profile is used. Multiple fragments can be separated by `|` (e.g. `Linn|ChorusDS|BubbleDS`). Otherwise the **Generic Device** profile applies as fallback.

#### How to find your device's user-agent

1. Open the **Debug** tab, tick **log debug information**, click **Save**.
2. Play something from the device.
3. Reopen the settings, go to Debug, click **View**. The log shows the device's user-agent on an "useragent=" line.

#### Per-profile fields

| Field | What it does |
|---|---|
| **name** | Display name of the profile. |
| **applies when the user-agent contains** | Matching fragments separated by `\|`. The first fragment found wins. |
| **maximum picture size** | Max pixel dimension for album art sent to this device. 160 is the DLNA standard; some devices accept larger. |
| **output sample rate (from / to)** | Allowed range for this device. Source files outside the range get sample-rate-converted (forces transcoding). |
| **channels: stereo only** | If the device only does 2-channel, this downmixes 5.1 / multi-channel to stereo. |
| **maximum bit depth** | 16 or 24. Higher than this gets bit-depth-converted. |
| **output format** | When transcoding is needed, transcode to this format. PCM-16 is safe; FLAC preserves quality at a smaller size; MP3/AAC/Ogg lose quality but save bandwidth. |
| **output sample rate** | Sample rate to use when transcoding. "Same as source" preserves the original. |

#### Settings for problem devices (also per profile)

These exist because UPnP spec compliance is patchy in real hardware. Most users never touch them.

| Setting | What it does | Symptom you'd see |
|---|---|---|
| **do not use RAW PCM** | Forces PCM wrapped in WAV (audio/wav) instead of raw L16/L24 (audio/L16, audio/L24). | Some Marantz, some older devices — white noise / silence when streaming raw PCM. |
| **force little endian for PCM streams** | UPnP PCM is big-endian per spec; some devices interpret it as little-endian and play noise. | White noise on PCM tracks. Tick this; usually fixes it. |
| **content length** | Controls the HTTP `Content-Length` header. `Default` = correct value. `None` = omit. `PCM Only` = only send for PCM. `Fixed` = send `4294959103` (LMS-compatible "infinite" value). | Problem devices that mishandle the length header — choose `None` or `Fixed` and test. |
| **force native stream (bypass transcoder)** | Send the original file bytes; **ignore device profile limits, EQ/DSP, ReplayGain, codec advertisement**. Defaults to **on**. (See *How these settings interact* above — when this is on, most other transcoding-related options become inert.) | Turn off if (a) the device truly can't decode some of your formats and needs forced transcoding, or (b) you want MusicBee's EQ/DSP or ReplayGain to apply to outgoing audio. Otherwise leave on — it's the most-impactful setting in the whole plugin. |

### How these settings interact — precedence

The plugin decides "transcode or not" by walking a fixed precedence chain. **Higher rules override lower ones.** This matters because several settings appear to do similar things and they don't always cooperate the way the labels suggest.

| Priority | Setting | Effect |
|---|---|---|
| 1 (highest) | **Force transcoding** (per profile, on its **Transcoding** sub-tab) | Always transcode for this device. Overrides everything below. The dialog treats it and force-native as **mutually exclusive** — ticking one unticks the other — so in practice you pick a lane per device. |
| 2 | **Force native stream** (per profile, default **ON**) | Never transcode for this device. **Suppresses rules 3-5 entirely.** Send the original file bytes regardless of format mismatch, DSP, ReplayGain, sample-rate limits. |
| 3 | **EQ/DSP** + **ReplayGain** (Library) | Force transcoding so the audio can be processed. **Silently ignored if rule 2 wins.** |
| 4 | **Sample rate / bit depth / stereo-only** in the device profile | Out-of-range source → transcode to fit. **Silently ignored if rule 2 wins.** |
| 5 (lowest) | Codec support per device | Source codec not in the device's advertised codecs → transcode to the profile's output format. **Silently ignored if rule 2 wins.** |

**Practical consequence**: with default settings (force-native ON on every profile), almost nothing else under "Library" or in the profile transcoding fields actually does anything. The plugin just hands the file to the device.

**If you want EQ/DSP or ReplayGain to actually apply**: turn off force-native-stream **on the profile of the device you're streaming to**. (Per-profile, not global — so you can keep force-native on for your hi-fi rig and off for a portable device that needs MusicBee's ReplayGain.)

**If you want to test "what does the renderer do with a fresh transcode"**: tick force-transcoding on that device's profile (Device Profiles → Transcoding). It wins over force-native — and the dialog unticks force-native for you, since the two are mutually exclusive.

### View (which nodes appear on the network, and how each is shaped)

The whole exposed tree as a checklist — Music, your filters, Podcasts, Audiobooks, Radio, the Inbox and Playlists. The pattern is always: **tick the nodes you want to act on**, then use the controls beside the tree to act on all of them at once.

| Action | What it does |
|---|---|
| **visible / hidden** | Whether the node appears at all on the other device. Hide what you never browse. |
| **pin to the root** | Lift a node to the top level instead of leaving it nested — e.g. put your Jazz filter beside Music. |
| **apply a path template** | Give the node a *discovery path* (see the Paths tab) — the route you drill down through it. Only templates that fit the node can be applied; the rest grey out. |

Radio and Podcasts are **permanently paired** with their own category templates, so you reshape them on the Paths tab rather than applying anything here.

### Paths (discovery path templates)

A **discovery path** is the route a browser walks inside a node: `Album Artist → Album → Tracks`, a flat track list, by Genre, by Year… A path is one or more **levels** (the field each groups by) ending in a **leaf** (albums-then-tracks, or a flat list). A node can carry several paths, so it offers more than one way in.

Templates are listed in two bands: **Standard** — where your own templates live and every new one is created — and **Reserved**, holding the Radio and Podcasts templates. The available grouping fields follow the band, because not every field means something for every node (a radio station has no album; a podcast episode has a subscription, a folder and a publish date but no album artist).

When a path ends in the **Album → Tracks** leaf, you also choose what counts as one album — pick one or more fields (Album, Sort Album, Year, Album Artist), each sortable ascending or descending, which together decide what fuses into one album, how its heading reads, and the order albums appear.

Defaults are sensible (new filters group by album artist; playlists and the Inbox get flat-ish defaults), so most people never open this tab.

### Debug

| Option | What it does | When to use |
|---|---|---|
| **log debug information** | Writes every SOAP call, every codec decision, every error to a log file. | On while troubleshooting; off otherwise (the log grows fast). |
| **clear log on plugin startup** | Starts a fresh log each time MusicBee launches. | On when logging long-term, so the file doesn't grow forever. |
| **View** / **Clear log** | Open the log in Notepad, or empty it. | After enabling logging. |
| **Read last query** / **Run query** / **Fetch tags for first 5** | Developer tools — replay the last library query, run one by hand, inspect the tags returned. | Diagnosing why a node browses wrong. Most users never touch these. |

---

## 4. yaiol-fork-only features

These don't exist in the original plugin or in UPnP 2025. They make the UPnP browsing experience much better when you have a big library.

### Filter tabs become UPnP root containers

If you have MusicBee filter tabs configured (the things across the top of MusicBee's main view — `MAIN`, `ALL`, `FAV`, `AI`, `REC`, etc.), they appear as **top-level entries** in your UPnP client.

This means a 300,000-track library doesn't dump you into a single overwhelming "Music" node. You get focused entry points — Jazz, Classical, Rock, Podcasts, Favorites, whatever filters you've set up — alongside the standard Music / Audiobooks / Radio / Playlists entries.

The filters are read live from `%AppData%\MusicBee\Filters\*.xautopf` — exactly the files MusicBee itself uses, so they stay in sync without configuration.

### The whole library is published, not just "Music"

Podcasts, Audiobooks, Radio stations, the Inbox and Playlists each appear as their own browsable container alongside the music — and each can be shown, hidden or pinned to the root individually (the **View** tab). Podcast subscriptions open to their episodes, with subscription artwork carried through. Most servers expose a flat music library and nothing else.

### Every node gets its own shape

Tapping a filter root drops you into **Album Artist → Album → Tracks** by default — with correct Disc/Track ordering inside each album, not alphabetical or random. But that shape is a *choice*: each node carries its own discovery path (see the **Paths** tab), so a playlist can browse as a flat ordered list, a filter by Genre or Year, and podcasts by subscription.

### Artists sorted by `Sort Album Artist`, not by display name

If your tags follow the convention `Artist = "Bob Dylan"`, `Sort Album Artist = "Dylan, Bob"`, the UPnP tree groups Dylan under **D**, not under **B**. The Sort field is used for both grouping and alphabetical display.

If a track has multiple Album Artists (e.g. `yaiol; Ars Ricercata`), it appears under **each** of them — same convention as multi-genre tracks.

### Album thumbnails in the UPnP tree

Album-container DIDL responses now include `upnp:albumArtURI`, so renderers that show artwork in their browse views actually do. The original plugin only included artwork on individual tracks.

### Coexists with the original plugin

The DLL is named `mb_UPnP_yaiol.dll` and the plugin announces itself as **MusicBee UPnP (yaiol)**, so it can live side-by-side with the original `mb_Upnp.dll` on disk. **Don't run both at once** — they bind the same UPnP port and same device UUID. Enable one in MusicBee's plugin manager, disable the other.

---

## 5. Quick troubleshooting

| Symptom | First thing to try |
|---|---|
| Device doesn't see MusicBee at all | Check Windows Firewall for both **private** and **public** networks. The plugin's port (9779 default) needs to be reachable. Restart MusicBee after firewall changes. |
| Device sees MusicBee but won't play any track | Profile for this device is probably wrong, OR force-native-stream is off and transcoding is failing. Tick **force native stream** in the device's profile and retry. |
| Audio plays as static / white noise | Try **force little endian for PCM streams** in the device's profile. If that doesn't help, try **do not use RAW PCM**. |
| Gaps between tracks | True gapless (`SetNextAVTransportURI` / NextURI) runs automatically on devices that support it. If you still hear gaps, the device either doesn't support it or its implementation is buggy — turn off NextURI for that device, or fall back to **output as a continuous stream** in Playback (gapless guaranteed, but track metadata and "next" controls break). |
| Modern device (2020+) doesn't respond to MusicBee's controls | The `MediaRenderer:3` advertisement fix is in this fork — should work. If not, post a log. |
| Plugin shows up in MusicBee but Configure doesn't open | Look at MusicBee's `ErrorLog.dat` — the plugin probably crashed on load. Usually a settings-file problem. Delete `%AppData%\MusicBee\UPnPSettings.ini` and reconfigure. |
| Languages other than English | 22 languages ship with the plugin (`main/locales/*.json`). Set **language** on the General tab, or leave it on "Auto" to follow MusicBee. |

---

## 6. What this fork doesn't do

Honestly, things you should know up front:

- **Gapless is young.** True gapless (`SetNextAVTransportURI` / NextURI) is implemented and runs automatically, but its renderer-specific edge cases have had limited hardware testing — see the hi-fi note below. Continuous-stream remains as a lossy fallback for devices that can't do NextURI.
- **No hi-fi hardware testing.** Testing was done with BubbleUPnP and foobar2000. True-gapless track-transition detection in particular was validated on BubbleUPnP; other renderers (WiiM, Sonos, Cambridge, Eversolo, Marantz, Denon) handle transitions slightly differently and should be tested by the community. If gapless playback misbehaves on a device, turn off NextURI for it so it falls back to playing one track at a time.

---

## 7. Architecture

A single VB.NET assembly — `mb_UPnP_yaiol.dll`, targeting **.NET Framework 4.7.2** (x86) — loaded in-process by MusicBee. No external services, no NuGet runtime dependencies; the only non-framework reference is the COM-interop `Interop.UPNPLib.dll`, a thin shim generated from Windows' built-in `upnp.dll` (the "UPnP 1.0 Type Library") and used only for router port-forwarding. Because that COM library is part of Windows, nothing beyond the single DLL ships to users.

### The UPnP / DLNA layering

UPnP is the generic plumbing; DLNA is the interop layer on top of it. The plugin speaks both:

| Layer | What the plugin uses it for |
|---|---|
| **SSDP** | Discovery — announcing MusicBee on the LAN and answering "who's there?" searches. |
| **SOAP** | Control actions (Browse, Play, SetVolume, …) — dispatched by reflection onto `UpnpService` subclasses. |
| **XML device/service descriptions** | The `/description.xml` (server) and `/renderer.xml` (renderer) documents that tell clients what each device is and can do. |
| **DIDL-Lite** | The XML that describes tracks / albums / artists in browse responses (album art via `upnp:albumArtURI`). |
| **DLNA conventions** | `DLNA.ORG_PN` profile codes, `contentFeatures.dlna.org`, seek flags, and the per-device transcode-profile system that negotiates formats. It declares `DMS-1.50` / `DMR-1.50` — **DLNA-compatible, not DLNA-Certified**. |

### How the three roles map onto UPnP

The plugin presents **two independent UPnP root devices**, both hosted on one shared HTTP server / port — plus a control point, which is a *client* and therefore not a device at all:

- **MediaServer** (DMS) — Role A, the library server. Implements `ContentDirectory` (browse the library) + `ConnectionManager`. This is the mature side.
- **MediaRenderer** (DMR) — Role C, MusicBee as the player. Implements `AVTransport` + `RenderingControl`. Plays both a **loopback** URI (one of our own `/Files/` or `/Encode/` URLs — short-circuited to the local library file, bit-perfect) and a **remote** `http(s)` URI cast from elsewhere (a phone, a NAS), which is handed to MusicBee's own streaming engine. It advertises `DMR-1.50` but eventing is subscribe-time-only (no live push) — polling control points work, full GENA push is future work.
- **Control point** — Role B, MusicBee driving someone else's renderer. Discovers renderers by M-SEARCH and calls *their* services; it publishes nothing itself. Accepts `MediaRenderer:1`, `:2` and `:3` advertisements, which is what makes post-2020 devices work.

Each role is independently gated by its own setting, so any combination runs. The renderer is a **separate root device with its own UUID and description document** (`/renderer.xml`), not a child of the server — combined-device control points would otherwise double-list it.

### Coexists with the original plugin

The assembly is deliberately named `mb_UPnP_yaiol.dll` and announces itself as **MusicBee UPnP (yaiol)**, distinct from the original `mb_Upnp.dll`, so both can sit in the Plugins folder. Don't enable both at once — they bind the same UPnP port and device UUID.

> Deeper protocol internals (the attribute-driven `UpnpService` dispatch, the shared-port device model, port self-healing, the settings-persistence format) are maintainer-only and live in the project's `CLAUDE.md` + `doc/`.

---

## 8. Installation

1. Download `mb_UPnP_yaiol.dll` from the [latest release](https://github.com/yaiol/musicbee-upnp-plugin/releases/latest).
2. Copy it into your MusicBee plugins folder, typically:
   `C:\Users\<you>\AppData\Roaming\MusicBee\Plugins\`
3. Restart MusicBee.
4. **Edit → Preferences → Plugins** → find **MusicBee UPnP (yaiol)** → click **Configure**.
5. If the original `mb_Upnp.dll` is also enabled, disable it.

---

## 9. Building from source

Requires Visual Studio 2019+ (Community is fine) and the .NET Framework 4.7.2 targeting pack. The COM-interop `Interop.UPNPLib.dll` is **committed to the repo** (under `bin/`), so a fresh clone builds with no extra setup. If you ever need to regenerate it, run `TlbImp.exe C:\Windows\System32\upnp.dll /out:bin\Interop.UPNPLib.dll /namespace:UPNPLib` from a Developer Command Prompt.

```
main/MusicBeeUpnp.sln
```

Build configuration: `Debug | x86` for local work → `main/bin/Debug/mb_UPnP_yaiol.dll`. Published releases are built `Release | x86` by CI: pushing a `v*` tag runs `.github/workflows/release.yml`, which builds on a Windows runner and creates the GitHub release with the DLL attached and the matching `CHANGELOG.md` section as the notes.

---

## 10. Credits

- Original plugin by **Steven Mayall**. The vast majority of this codebase is his work.
- The UPnP 2025 fork by **BoringName** for ideas and bug catalogs.
