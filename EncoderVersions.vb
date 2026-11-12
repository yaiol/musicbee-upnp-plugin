Imports System.Text
Imports System.IO
Imports System.Diagnostics
Imports System.Text.RegularExpressions
Imports System.Net
Imports System.Web.Script.Serialization

Partial Public Class Plugin

    ''' <summary>
    ''' Answers "which encoder programs does this MusicBee have, and which version is each?".
    '''
    ''' Every transcoded format except PCM/Wave is produced by a program in the "Codec" folder
    ''' beside MusicBee.exe. MusicBee ships most of them and never updates them, so their age is
    ''' invisible until something sounds wrong. This only READS: it asks each program for its own
    ''' version line. It installs, replaces and downloads nothing - that folder is MusicBee's, and
    ''' on a standard install it cannot be written without administrator rights.
    '''
    ''' The CURRENT version of each program comes from a small list the plugin's site publishes
    ''' (versions and download pages only). It is fetched when the tab is shown; without it the
    ''' tab still shows what is installed.
    ''' </summary>
    Friend NotInheritable Class EncoderVersions

        Friend NotInheritable Class Entry
            ''' <summary>The program's file name, as MusicBee's command lines name it.</summary>
            Public FileName As String
            ''' <summary>The format it produces - a format name, the same in every language.</summary>
            Public Format As String
            ''' <summary>False when the file is not in the folder (AAC's encoder is not shipped).</summary>
            Public Present As Boolean
            ''' <summary>The version the program reports; empty when it gave none we could read.</summary>
            Public Version As String = ""
            ''' <summary>32 or 64 (the program's own build), 0 when unknown.</summary>
            Public Bits As Integer
        End Class

        ''' <summary>What the project currently publishes for one program.</summary>
        Friend NotInheritable Class Current
            Public Version As String = ""
            ''' <summary>The page a user gets it from.</summary>
            Public Download As String = ""
        End Class

        ''' <summary>The published list: program file name -> what is current. Empty on any failure.</summary>
        Friend Shared Function FetchCurrent() As Dictionary(Of String, Current)
            Dim result As New Dictionary(Of String, Current)(StringComparer.OrdinalIgnoreCase)
            Try
                Try
                    ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol Or SecurityProtocolType.Tls12
                Catch
                End Try
                Dim json As String
                Using wc As New WebClient()
                    wc.Headers.Add("User-Agent", UpdateCheck.AppId & "/" & UpdateCheck.DisplayVersion())
                    wc.Encoding = Encoding.UTF8
                    json = wc.DownloadString(UpdateCheck.SITE & "/data/codec.json")
                End Using
                Dim root As Dictionary(Of String, Object) = TryCast(New JavaScriptSerializer().DeserializeObject(json), Dictionary(Of String, Object))
                If root Is Nothing OrElse Not root.ContainsKey("encoders") Then Return result
                Dim encoders As Dictionary(Of String, Object) = TryCast(root("encoders"), Dictionary(Of String, Object))
                If encoders Is Nothing Then Return result
                For Each pair As KeyValuePair(Of String, Object) In encoders
                    Dim fields As Dictionary(Of String, Object) = TryCast(pair.Value, Dictionary(Of String, Object))
                    If fields Is Nothing Then Continue For
                    Dim c As New Current()
                    If fields.ContainsKey("version") Then c.Version = Convert.ToString(fields("version"))
                    If fields.ContainsKey("download") Then c.Download = Convert.ToString(fields("download"))
                    If c.Version.Length > 0 Then result(pair.Key) = c
                Next pair
            Catch ex As Exception
                ' No network, or the site is unreachable: the tab shows what is installed and nothing else.
                LogError(ex, "EncoderVersions.FetchCurrent")
            End Try
            Return result
        End Function

        ''' <summary>
        ''' True when the installed program is older than the current one. Only the leading numbers are
        ''' compared: a build names itself with more than its release number (opus-tools 0.2 reports
        ''' "0.2-3-gf5f571b"), and that tail says nothing about being older or newer than "0.2".
        ''' </summary>
        Friend Shared Function IsBehind(installed As String, currentVersion As String) As Boolean
            Dim a As String = LeadingNumbers(installed)
            Dim b As String = LeadingNumbers(currentVersion)
            If a.Length = 0 OrElse b.Length = 0 Then Return False
            Return UpdateCheck.IsNewer(b, a)
        End Function

        Private Shared ReadOnly leadingNumbersRegex As New Regex("^\d+(\.\d+)*", RegexOptions.CultureInvariant)

        Private Shared Function LeadingNumbers(version As String) As String
            If String.IsNullOrEmpty(version) Then Return ""
            Return leadingNumbersRegex.Match(version.Trim()).Value
        End Function

        ''' <summary>One known encoder: its file, its format, how to ask for its version, where the answer is.</summary>
        Private NotInheritable Class Known
            Public FileName As String
            Public Format As String
            Public Arguments As String
            Public Pattern As Regex
            Public Sub New(fileName As String, format As String, arguments As String, pattern As String)
                Me.FileName = fileName
                Me.Format = format
                Me.Arguments = arguments
                Me.Pattern = New Regex(pattern, RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
            End Sub
        End Class

        ' The programs MusicBee's file converters name. Each prints its version when asked (Takc
        ' and the Nero encoder print it in their usage text, with no version option of their own).
        Private Shared ReadOnly KnownEncoders() As Known = New Known() {
            New Known("lame.exe", "MP3", "--version", "version\s+([\d.]+)"),
            New Known("neroAacEnc.exe", "AAC", "-help", "Package version:\s*([\d.]+)"),
            New Known("oggenc2.exe", "Ogg Vorbis", "--version", "OggEnc\s+v([\d.]+)"),
            New Known("opusenc.exe", "Opus", "--version", "opus-tools\s+([\w.\-]+)"),
            New Known("flac.exe", "FLAC", "--version", "flac\s+([\d.]+)"),
            New Known("wavpack.exe", "WavPack", "--version", "wavpack\s+([\d.]+)"),
            New Known("mpcenc.exe", "Musepack", "--version", "MPC Encoder\s+([\d.]+)"),
            New Known("Takc.exe", "TAK", "", "TAKC\s+([\d.]+)")}

        ''' <summary>MusicBee's encoder folder: "Codec" beside the running MusicBee.exe.</summary>
        Friend Shared Function CodecFolder() As String
            Return Path.Combine(System.Windows.Forms.Application.StartupPath, "Codec")
        End Function

        ''' <summary>Every known encoder, in a fixed order, present or not. Runs each present one once.</summary>
        Friend Shared Function ReadAll() As List(Of Entry)
            Dim folder As String = CodecFolder()
            Dim list As New List(Of Entry)
            For Each k As Known In KnownEncoders
                Dim e As New Entry With {.FileName = k.FileName, .Format = k.Format}
                Dim exePath As String = Path.Combine(folder, k.FileName)
                e.Present = File.Exists(exePath)
                If e.Present Then
                    e.Bits = ProgramBits(exePath)
                    Try
                        Dim m As Match = k.Pattern.Match(RunForText(exePath, k.Arguments))
                        If m.Success Then e.Version = m.Groups(1).Value.TrimEnd("."c)
                    Catch ex As Exception
                        LogError(ex, "EncoderVersions.ReadAll", k.FileName)
                    End Try
                End If
                list.Add(e)
            Next k
            Return list
        End Function

        ' Start the program with nothing to encode and collect what it prints (both streams: some
        ' write their banner to the error stream). It is given no input and a few seconds; one that
        ' is still running after that is stopped, and what it printed so far is used.
        Private Shared Function RunForText(exePath As String, arguments As String) As String
            Dim psi As New ProcessStartInfo(exePath, arguments) With {
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardInput = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .WorkingDirectory = Path.GetDirectoryName(exePath)}
            Dim text As New StringBuilder()
            Using p As New Process()
                p.StartInfo = psi
                AddHandler p.OutputDataReceived, Sub(s, a)
                                                     If a.Data IsNot Nothing Then
                                                         SyncLock text
                                                             text.AppendLine(a.Data)
                                                         End SyncLock
                                                     End If
                                                 End Sub
                AddHandler p.ErrorDataReceived, Sub(s, a)
                                                    If a.Data IsNot Nothing Then
                                                        SyncLock text
                                                            text.AppendLine(a.Data)
                                                        End SyncLock
                                                    End If
                                                End Sub
                p.Start()
                p.BeginOutputReadLine()
                p.BeginErrorReadLine()
                p.StandardInput.Close()
                If p.WaitForExit(4000) Then
                    p.WaitForExit()   ' lets the two readers finish
                Else
                    Try
                        p.Kill()
                    Catch
                    End Try
                End If
            End Using
            SyncLock text
                Return text.ToString()
            End SyncLock
        End Function

        ' 32 or 64, from the program file's own header (the "machine" field of its PE header).
        Private Shared Function ProgramBits(exePath As String) As Integer
            Try
                Using fs As New FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Using r As New BinaryReader(fs)
                        If fs.Length < 64 Then Return 0
                        fs.Position = 60
                        Dim headerAt As Integer = r.ReadInt32()
                        If headerAt <= 0 OrElse headerAt + 6 > fs.Length Then Return 0
                        fs.Position = headerAt
                        If r.ReadUInt32() <> &H4550UI Then Return 0   ' "PE\0\0"
                        Select Case r.ReadUInt16()
                            Case &H14CUS : Return 32
                            Case &H8664US, &HAA64US : Return 64
                        End Select
                    End Using
                End Using
            Catch ex As Exception
                LogError(ex, "EncoderVersions.ProgramBits", exePath)
            End Try
            Return 0
        End Function

    End Class

End Class
