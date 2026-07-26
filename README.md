# MusicBee UPnP — yaiol fork

Open-source fork of Steven Mayall's original MusicBee UPnP plugin. Adds richer library navigation (filter-based root containers, AlbumArtist-sort grouping, album artwork in DIDL responses) and ports forward the most useful playback fixes from the closed-source [UPnP 2025 fork](https://getmusicbee.com/addons/plugins/534/upnp-2025/).

This README explains what every option in the settings dialog actually does. UPnP/DLNA terminology is largely the original authors' jargon; the goal here is to translate it into plain English so you know what to tick.

---

## 1. What this plugin does

UPnP/DLNA is a network protocol that lets devices on your local network share and play media without any cloud or login. This plugin gives MusicBee two roles:

### Role A — "Library server"

Your phone, smart TV, hi-fi streamer or speaker can browse MusicBee's music library over the network and play tracks from it. MusicBee acts like a Spotify-on-LAN: you point a UPnP app (BubbleUPnP on Android, Hi-Fi Cast, mconnect, Symfonium with Subsonic — *not* with UPnP, etc.) at MusicBee and it sees your library.

This is the **incoming** direction — clients pull from MusicBee.

### Role B — "Player / controller"

You can pick a UPnP device on your network as MusicBee's output. Press Play in MusicBee, sound comes out of your speaker / streamer / TV.

This is the **outgoing** direction — MusicBee pushes to a renderer.

You can use one role, the other, or both. The settings dialog has separate sections for each.

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
| **language** | Picks the plugin's own UI language. "Auto" follows MusicBee. | Only change if MusicBee is in one language but you want the plugin in another. |
| **server name** | The name your other devices see when they discover MusicBee on the network. | Whatever you want. Default is fine. |
| **IP address** | Which network interface MusicBee binds the server to. "Automatic" picks all interfaces. | Multi-network PCs (Wi-Fi + Ethernet + VPN): pick the one your devices are on. |
| **port** | TCP port for the HTTP server. 49382 by default. | Only if another app is already using 49382, or your firewall demands a specific port. |

### Playback (outgoing — MusicBee → renderer)

| Option | What it does | When to change |
|---|---|---|
| **enable MusicBee to play to a UPnP device** | Turns the controller role on/off. When on, UPnP devices appear in MusicBee's Player settings as output choices. | Off if you don't push from MusicBee to anything. |
| **output as a continuous stream** | Sends one infinite audio stream instead of one file per track. Eliminates inter-track gaps. **Tradeoff**: the renderer shows the FIRST track's metadata for the entire session, "next track" buttons stop working, native streaming is disabled (everything must be transcoded). | Last-resort gapless workaround. Better to use a renderer that supports `SetNextAVTransportURI` (NextURI). |

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

1. Open the **Diagnostics** section, tick **log debug information**, click **Save**.
2. Play something from the device.
3. Reopen the settings, go to Diagnostics, click **View**. The log shows the device's user-agent on an "useragent=" line.

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
| 1 (highest) | **Force transcoding** (Diagnostics, global) | Always transcode. Overrides everything below. Rarely useful — mostly a diagnostic toggle. |
| 2 | **Force native stream** (per profile, default **ON**) | Never transcode for this device. **Suppresses rules 3-5 entirely.** Send the original file bytes regardless of format mismatch, DSP, ReplayGain, sample-rate limits. |
| 3 | **EQ/DSP** + **ReplayGain** (Library) | Force transcoding so the audio can be processed. **Silently ignored if rule 2 wins.** |
| 4 | **Sample rate / bit depth / stereo-only** in the device profile | Out-of-range source → transcode to fit. **Silently ignored if rule 2 wins.** |
| 5 (lowest) | Codec support per device | Source codec not in the device's advertised codecs → transcode to the profile's output format. **Silently ignored if rule 2 wins.** |

**Practical consequence**: with default settings (force-native ON on every profile), almost nothing else under "Library" or in the profile transcoding fields actually does anything. The plugin just hands the file to the device.

**If you want EQ/DSP or ReplayGain to actually apply**: turn off force-native-stream **on the profile of the device you're streaming to**. (Per-profile, not global — so you can keep force-native on for your hi-fi rig and off for a portable device that needs MusicBee's ReplayGain.)

**If you want to test "what does the renderer do with a fresh transcode"**: tick force-transcoding (global, Diagnostics). It overrides everything including force-native.

### Diagnostics

| Option | What it does | When to use |
|---|---|---|
| **network is bandwidth constrained** | If transcoding kicks in, use the device profile's *lossy* output (MP3/AAC/Ogg) regardless. | On if you stream over a slow Wi-Fi link. |
| **log debug information** | Writes every SOAP call, every codec decision, every error to a log file. | On while troubleshooting; off otherwise (the log grows fast). |
| **View** | Opens the log file in Notepad. | After enabling logging. |
| **force transcoding (global setting — every stream is transcoded)** | Every track gets re-encoded through the profile's transcode codec, no matter what. | Rarely useful — only for diagnosing whether the source format is the problem. |

---

## 4. yaiol-fork-only features

These don't exist in the original plugin or in UPnP 2025. They make the UPnP browsing experience much better when you have a big library.

### Filter tabs become UPnP root containers

If you have MusicBee filter tabs configured (the things across the top of MusicBee's main view — `MAIN`, `ALL`, `FAV`, `AI`, `REC`, etc.), they appear as **top-level entries** in your UPnP client.

This means a 300,000-track library doesn't dump you into a single overwhelming "Music" node. You get focused entry points — Jazz, Classical, Rock, Podcasts, Favorites, whatever filters you've set up — alongside the standard Music / Audiobooks / Radio / Playlists entries.

The filters are read live from `%AppData%\MusicBee\Filters\*.xautopf` — exactly the files MusicBee itself uses, so they stay in sync without configuration.

### Inside each filter: `Album Artist → Album → Tracks`

Tapping a filter root drops you into the **Album Artist** list, then **Album**, then **Tracks**. Track ordering inside each album is correct (Disc/Track number), not alphabetical or random.

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
| Device doesn't see MusicBee at all | Check Windows Firewall for both **private** and **public** networks. The plugin's port (49382 default) needs to be reachable. Restart MusicBee after firewall changes. |
| Device sees MusicBee but won't play any track | Profile for this device is probably wrong, OR force-native-stream is off and transcoding is failing. Tick **force native stream** in the device's profile and retry. |
| Audio plays as static / white noise | Try **force little endian for PCM streams** in the device's profile. If that doesn't help, try **do not use RAW PCM**. |
| Gaps between tracks | True gapless (`SetNextAVTransportURI` / NextURI) runs automatically on devices that support it. If you still hear gaps, the device either doesn't support it or its implementation is buggy — turn off NextURI for that device, or fall back to **output as a continuous stream** in Playback (gapless guaranteed, but track metadata and "next" controls break). |
| Modern device (2020+) doesn't respond to MusicBee's controls | The `MediaRenderer:3` advertisement fix is in this fork — should work. If not, post a log. |
| Plugin shows up in MusicBee but Configure doesn't open | Look at MusicBee's `ErrorLog.dat` — the plugin probably crashed on load. Usually a settings-file problem. Delete `%AppData%\MusicBee\UPnPSettings.ini` and reconfigure. |
| Languages other than English | Currently only English is bundled. The infrastructure is there for adding translations as separate `.resx` files — contributions welcome. |

---

## 6. What this fork doesn't do

Honestly, things you should know up front:

- **Gapless is young.** True gapless (`SetNextAVTransportURI` / NextURI) is implemented and runs automatically, but its renderer-specific edge cases have had limited hardware testing — see the hi-fi note below. Continuous-stream remains as a lossy fallback for devices that can't do NextURI.
- **No podcast support.** UPnP 2025 added Podcasts as a root container; not ported here. (Pull request welcome.)
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

### The two roles

The plugin presents **two independent UPnP root devices**, both hosted on one shared HTTP server / port:

- **MediaServer** (DMS) — the "library server" role. Implements `ContentDirectory` (browse the library) + `ConnectionManager`. This is the mature side.
- **MediaRenderer** (DMR) — the "player / controller" role. Implements `AVTransport` + `RenderingControl`. Phase 1: it advertises `DMR-1.50` but eventing is subscribe-time-only (no live push) — polling control points work, full GENA push is future work.

Each role is independently gated by a setting, so you can run server-only, renderer-only, or both.

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
