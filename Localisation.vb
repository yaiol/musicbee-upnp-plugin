Imports System.Globalization
Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Threading

Partial Public Class Plugin

    ' Localisation - detects the MusicBee UI language, applies it to the current thread's UICulture,
    ' and loads the matching embedded JSON string bundle so Plugin.L(key) returns translated strings.
    '
    ' MusicBee stores the selected language as <SystemLanguage>Endonym</SystemLanguage> inside
    ' %AppData%\MusicBee\MusicBee3Settings.ini. The tag is OMITTED when language is the default English.
    ' The endonym matches what's shown in MusicBee's language dropdown (e.g. "Français", "Русский",
    ' "日本語"). We map endonyms to .NET culture codes.
    Friend NotInheritable Class Localisation

        Private Sub New()
        End Sub

        ' Endonym (as stored in MusicBee3Settings.ini) → .NET culture code.
        ' Order matches MusicBee's dropdown for readability.
        Private Shared ReadOnly endonymToCulture As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
            {"Arabic", "ar"},
            {"Czech", "cs"},
            {"Deutsch", "de"},
            {"English", "en"},
            {"English(US)", "en-US"},
            {"Español", "es"},
            {"Français", "fr"},
            {"Greek", "el"},
            {"Hungarian", "hu"},
            {"Italiano", "it"},
            {"Korean", "ko"},
            {"Nederlands", "nl"},
            {"Norsk", "nb"},
            {"Polski", "pl"},
            {"Português (BR)", "pt-BR"},
            {"Português (PT)", "pt-PT"},
            {"Svenska", "sv"},
            {"Turkish", "tr"},
            {"Ukrainian", "uk"},
            {"Русский", "ru"},
            {"日本語", "ja"},
            {"简体中文", "zh-CN"},
            {"繁体中文", "zh-TW"}
        }

        Private Shared ReadOnly systemLanguageRegex As New Regex("<SystemLanguage>([^<]+)</SystemLanguage>", RegexOptions.Compiled Or RegexOptions.CultureInvariant)

        ' Reads MusicBee's selected language from its settings file. Returns the .NET culture code,
        ' or "en" when the tag is missing (MusicBee's default-English case) or unmapped.
        Public Shared Function DetectMusicBeeLanguage() As String
            Try
                Dim settingsPath As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicBee", "MusicBee3Settings.ini")
                If Not File.Exists(settingsPath) Then
                    Return "en"
                End If
                ' The file is binary with embedded XML text. Read as raw bytes and ASCII-decode the searchable region.
                Dim bytes() As Byte = File.ReadAllBytes(settingsPath)
                Dim content As String = System.Text.Encoding.UTF8.GetString(bytes)
                Dim match As Match = systemLanguageRegex.Match(content)
                If Not match.Success Then
                    Return "en"
                End If
                Dim endonym As String = match.Groups(1).Value
                Dim culture As String = Nothing
                If endonymToCulture.TryGetValue(endonym, culture) Then
                    Return culture
                End If
                Return "en"
            Catch ex As Exception
                Return "en"
            End Try
        End Function

        ' Detects MusicBee's UI language and loads the matching string bundle onto the current thread.
        ' Called once during Plugin.Initialise before any string lookup happens. The plugin always
        ' follows MusicBee's language - there is no separate plugin-side language override.
        '
        ' Falls back to Thread.CurrentThread.CurrentUICulture (Windows default) on any error.
        Public Shared Sub Apply()
            Try
                Dim cultureCode As String = DetectMusicBeeLanguage()
                Dim culture As New CultureInfo(cultureCode)
                Thread.CurrentThread.CurrentUICulture = culture
                Plugin.LoadStrings(cultureCode)
            Catch ex As Exception
                ' Leave the default UICulture in place. Don't crash plugin load over a localisation failure.
            End Try
        End Sub

    End Class

End Class
