Imports System.IO
Imports System.Reflection
Imports System.Web.Script.Serialization

Partial Public Class Plugin

    ' Localized string store - replaces the old My.Resources.Resources.* (resx) lookup.
    '
    ' Every language ships as a flat JSON bundle embedded in THIS DLL (locales\<culture>.json,
    ' manifest resource name "MusicBeePlugin.<culture>.json" - VB names embedded resources
    ' RootNamespace + filename, dropping the folder segment). There are NO satellite
    ' assemblies and NO culture subfolders - one DLL carries every language. At startup
    ' LoadStrings() loads the English base plus the user's culture bundle; L(key) returns the
    ' culture value, falling back to English, then to the key itself.
    Private Shared enStrings As Dictionary(Of String, String)
    Private Shared localeStrings As Dictionary(Of String, String)

    ' Reads and parses one embedded locale bundle. Returns Nothing if absent/unparseable.
    Private Shared Function LoadBundle(cultureCode As String) As Dictionary(Of String, String)
        If String.IsNullOrEmpty(cultureCode) Then Return Nothing
        Try
            Dim asm As Assembly = Assembly.GetExecutingAssembly()
            Dim suffix As String = "." & cultureCode & ".json"
            Dim resName As String = asm.GetManifestResourceNames().FirstOrDefault(Function(n) n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            If resName Is Nothing Then Return Nothing
            Using stream As Stream = asm.GetManifestResourceStream(resName)
                If stream Is Nothing Then Return Nothing
                Using reader As New StreamReader(stream, System.Text.Encoding.UTF8)
                    Dim ser As New JavaScriptSerializer()
                    ser.MaxJsonLength = Integer.MaxValue
                    Return ser.Deserialize(Of Dictionary(Of String, String))(reader.ReadToEnd())
                End Using
            End Using
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

    ' Loads the English base bundle plus the requested culture's bundle. For a region culture
    ' (e.g. "pt-BR") with no exact bundle, falls back to the 2-letter language ("pt"). English
    ' or unknown cultures leave localeStrings empty so L() returns the English base.
    Friend Shared Sub LoadStrings(cultureCode As String)
        If enStrings Is Nothing Then enStrings = LoadBundle("en")
        If enStrings Is Nothing Then enStrings = New Dictionary(Of String, String)()

        localeStrings = Nothing
        If String.IsNullOrEmpty(cultureCode) Then Return
        If String.Equals(cultureCode, "en", StringComparison.OrdinalIgnoreCase) Then Return

        localeStrings = LoadBundle(cultureCode)
        If localeStrings Is Nothing AndAlso cultureCode.Contains("-") Then
            localeStrings = LoadBundle(cultureCode.Split("-"c)(0))
        End If
    End Sub

    ' Returns the localized string for key: current culture, else English, else the key itself.
    Friend Shared Function L(key As String) As String
        If enStrings Is Nothing Then LoadStrings("en")
        Dim v As String = Nothing
        If localeStrings IsNot Nothing AndAlso localeStrings.TryGetValue(key, v) Then Return v
        If enStrings IsNot Nothing AndAlso enStrings.TryGetValue(key, v) Then Return v
        Return key
    End Function

End Class
