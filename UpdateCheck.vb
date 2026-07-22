Imports System.Net
Imports System.Reflection
Imports System.Text.RegularExpressions

Partial Public Class Plugin

    ' Update check - polls the version-only beacon at apps.yaiol.com and builds the
    ' localized "What's new" / "Download" page URLs. Mirrors the Electron apps'
    ' update-check.js: the beacon carries only { "version": "x.y.z" }; the release
    ' notes and download buttons live on the localized release pages, not in the
    ' beacon. The check is best-effort - any failure (404 / offline / malformed)
    ' silently no-ops so it never blocks or errors the settings dialog.
    Friend NotInheritable Class UpdateCheck

        Private Sub New()
        End Sub

        Private Const SITE As String = "https://apps.yaiol.com"
        Private Const ENDPOINT As String = "https://apps.yaiol.com/p"

        ' Languages the website is actually published in - the plugin localizes into
        ' more, but release pages only exist for these; fall back to en otherwise.
        ' (Keep in sync with update-check.js SITE_LANGS.)
        Private Shared ReadOnly SiteLangs As String() = New String() {"en", "fr", "es", "de"}

        Private Shared ReadOnly versionRegex As New Regex("""version""\s*:\s*""([^""]+)""", RegexOptions.Compiled Or RegexOptions.CultureInvariant)

        ' The build version from <Assembly: AssemblyFileVersion> (e.g. "2.1.2.0"). This is the value
        ' /git bumps on every commit; AssemblyVersion is intentionally pinned at 2.0.0.0 for binary
        ' compatibility, so it must NOT be used for the displayed or compared version.
        ' ⚠ CLAUDE: read AssemblyFileVersion here - NEVER Assembly.GetName().Version (that returns the
        ' pinned AssemblyVersion 2.0.0.0, which froze the title + update-check + MusicBee plugin list
        ' at 2.0.0). CurrentVersion / DisplayVersion / the PluginInfo version all flow through this.
        Private Shared Function FileVersionString() As String
            Dim attrs As Object() = Assembly.GetExecutingAssembly().GetCustomAttributes(GetType(AssemblyFileVersionAttribute), False)
            If attrs IsNot Nothing AndAlso attrs.Length > 0 Then
                Return DirectCast(attrs(0), AssemblyFileVersionAttribute).Version
            End If
            Return "0.0.0.0"
        End Function

        ' The plugin's brand name from <Assembly: AssemblyProduct> (kept in sync with info.json by
        ' app-info). ⚠ CLAUDE: the display name is BRAND - language-invariant, read from the assembly
        ' here as the SINGLE source. It is deliberately NOT localized: never re-add a per-locale
        ' "PluginName" key (see app/CLAUDE-localization.md - localize the description, not the name).
        Public Shared Function ProductName() As String
            Dim attrs As Object() = Assembly.GetExecutingAssembly().GetCustomAttributes(GetType(AssemblyProductAttribute), False)
            If attrs IsNot Nothing AndAlso attrs.Length > 0 Then
                Return DirectCast(attrs(0), AssemblyProductAttribute).Product
            End If
            Return "MusicBee UPnP"
        End Function

        ' The running plugin version (AssemblyFileVersion), compared against the beacon.
        Public Shared Function CurrentVersion() As String
            Try
                Return FileVersionString()
            Catch
                Return "0.0.0"
            End Try
        End Function

        ' Trimmed version for display (Major.Minor.Build, e.g. "2.1.2") - used in the settings
        ' dialog title. The full 4-part CurrentVersion() is what the beacon comparison uses.
        Public Shared Function DisplayVersion() As String
            Try
                Return New Version(FileVersionString()).ToString(3)
            Catch
                Return CurrentVersion()
            End Try
        End Function

        ' Fetches https://apps.yaiol.com/p/<appId>/latest.json and extracts "version".
        ' Returns False on any failure - caller no-ops.
        Public Shared Function TryGetLatestVersion(appId As String, ByRef version As String) As Boolean
            version = Nothing
            Try
                Try
                    ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol Or SecurityProtocolType.Tls12
                Catch
                End Try
                Using wc As New WebClient()
                    wc.Headers.Add("User-Agent", "musicbee-upnp-yaiol")
                    Dim json As String = wc.DownloadString(ENDPOINT & "/" & appId & "/latest.json")
                    Dim m As Match = versionRegex.Match(json)
                    If Not m.Success Then Return False
                    version = m.Groups(1).Value.Trim()
                    Return Not String.IsNullOrEmpty(version)
                End Using
            Catch
                Return False
            End Try
        End Function

        ' Numeric dotted-segment comparison ("2.0.10" > "2.0.9"). True if latest > current.
        Public Shared Function IsNewer(latest As String, current As String) As Boolean
            Dim a As Integer() = ParseParts(latest)
            Dim b As Integer() = ParseParts(current)
            Dim n As Integer = Math.Max(a.Length, b.Length)
            For i As Integer = 0 To n - 1
                Dim av As Integer = If(i < a.Length, a(i), 0)
                Dim bv As Integer = If(i < b.Length, b(i), 0)
                If av <> bv Then Return av > bv
            Next
            Return False
        End Function

        Private Shared Function ParseParts(v As String) As Integer()
            If String.IsNullOrEmpty(v) Then Return New Integer() {}
            Dim segs As String() = v.Split("."c)
            Dim out(segs.Length - 1) As Integer
            For i As Integer = 0 To segs.Length - 1
                Dim parsed As Integer
                Integer.TryParse(segs(i), parsed)
                out(i) = parsed
            Next
            Return out
        End Function

        ' Map a .NET culture code to a published site language (base, en fallback).
        Public Shared Function SiteLang(cultureCode As String) As String
            If String.IsNullOrEmpty(cultureCode) Then Return "en"
            Dim baseCode As String = cultureCode.Split("-"c)(0).ToLowerInvariant()
            For Each l As String In SiteLangs
                If l = baseCode Then Return baseCode
            Next
            Return "en"
        End Function

        ' Localized release page URL. page = "latest" (What's new) or "download".
        ' Both pages carry identical content by design.
        Public Shared Function PageUrl(appId As String, cultureCode As String, page As String) As String
            Return SITE & "/" & SiteLang(cultureCode) & "/p/" & appId & "/" & page & ".html"
        End Function

        ' Localized help page URL - apps.yaiol.com/<lang>/p/<appId>/help/.
        ' Sends the FULL UI culture (already hyphenated by .NET, e.g. pt-BR/zh-CN),
        ' NOT the collapsed site-language: help is published in far more languages
        ' than the 4 showcase-chrome langs, and nginx falls back to the EN help page
        ' for any language not built. This mirrors the Electron / browser-ext help
        ' button, which sends its full UI language (hyphen-normalized). SiteLang's
        ' collapse-to-base is only right for PageUrl (release/download pages, which
        ' exist solely in the 4 showcase languages) - do not use it here.
        Public Shared Function HelpUrl(appId As String, cultureCode As String) As String
            Dim webLang As String = If(String.IsNullOrEmpty(cultureCode), "en", cultureCode)
            Return SITE & "/" & webLang & "/p/" & appId & "/help/"
        End Function

    End Class

End Class
