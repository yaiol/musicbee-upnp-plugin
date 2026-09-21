Imports System.Text
Imports System.IO
Imports System.Diagnostics
Imports System.Threading

Partial Public Class Plugin

    ''' <summary>
    ''' Answers "will this output format actually produce audio on this machine?".
    '''
    ''' Every non-PCM output format is produced by an external executable configured in
    ''' MusicBee > Preferences > File Converters. Three things can be wrong with that, and only
    ''' the first two can be seen without running it:
    '''   1. MusicBee has no converter for the codec at all.
    '''   2. The executable it names is not on disk (AAC ships no encoder by default).
    '''   3. The executable is there and starts, and still writes nothing - because the
    '''      parameters are wrong (a stale "-p 4" in the Ogg row broke it for years), or because
    '''      the format cannot be produced on a pipe at all (neroAacEnc writes MP4, which needs
    '''      to seek back to finalise its header).
    '''
    ''' Case 3 is the one that cost a morning to find, and nothing short of actually running the
    ''' encoder detects it - so that is what this does, on half a second of generated audio.
    ''' </summary>
    Friend NotInheritable Class EncoderCheck

        Friend Enum Status
            ''' <summary>Encoded by BASS in-process; no external program involved, cannot fail this way.</summary>
            Builtin
            ''' <summary>Ran the real command line and got audio out.</summary>
            Ok
            ''' <summary>MusicBee has no file converter configured for this codec.</summary>
            NoConverter
            ''' <summary>The command line names an executable that is not on disk.</summary>
            ExecutableMissing
            ''' <summary>The encoder ran and exited without writing a single byte.</summary>
            ProducedNothing
            ''' <summary>The encoder crashed - Windows terminated it with an exception code.</summary>
            Crashed
            ''' <summary>The test itself failed (could not launch, timed out).</summary>
            TestFailed
        End Enum

        Friend NotInheritable Class Result
            Public Codec As FileCodec
            Public Status As Status
            Public CommandLine As String
            ''' <summary>Encoder's own words - the reason, when it gave one.</summary>
            Public Detail As String
            Public BytesProduced As Long
            ''' <summary>The encoder's exit code. A Windows crash shows up here as 0xC0000005 etc.</summary>
            Public ExitCode As Integer

            Public ReadOnly Property IsUsable As Boolean
                Get
                    Return Status = Status.Builtin OrElse Status = Status.Ok
                End Get
            End Property
        End Class

        ''' <summary>Formats the settings dialog offers as an output format, in dropdown order.</summary>
        Friend Shared ReadOnly OfferedCodecs() As FileCodec = New FileCodec() {
            FileCodec.Pcm, FileCodec.Mp3, FileCodec.Aac, FileCodec.Ogg, FileCodec.Flac}

        Friend Shared Function CheckAll() As List(Of Result)
            Dim results As New List(Of Result)
            For Each codec As FileCodec In OfferedCodecs
                results.Add(Check(codec))
            Next codec
            Return results
        End Function

        Friend Shared Function Check(codec As FileCodec) As Result
            Dim result As New Result With {.Codec = codec}

            ' PCM and Wave are encoded by BASS inside our own process - no command line, no
            ' external executable, nothing that can be misconfigured.
            If codec = FileCodec.Pcm OrElse codec = FileCodec.Wave OrElse codec = FileCodec.AnyPcm Then
                result.Status = Status.Builtin
                Return result
            End If

            Dim quality As EncodeQuality
            Dim commandLine As String = AudioEncoder.GetConvertCommandLine(codec, quality, "EncoderCheck")
            If String.IsNullOrEmpty(commandLine) Then
                result.Status = Status.NoConverter
                Return result
            End If
            result.CommandLine = commandLine

            Dim exePath As String = Nothing
            Dim arguments As String = Nothing
            If Not TrySplitCommandLine(commandLine, exePath, arguments) Then
                result.Status = Status.TestFailed
                result.Detail = "could not read an executable out of the command line"
                Return result
            End If
            If Not File.Exists(exePath) Then
                result.Status = Status.ExecutableMissing
                result.Detail = exePath
                Return result
            End If

            ' Run exactly what the streaming path would run: same command line, same
            ' [outputfile] -> "-" substitution, audio on stdin, encoded bytes expected on stdout.
            ' Logged either side of the launch: if the encoder takes the process down with it,
            ' the "running" line is the last thing written and names which one it was.
            Dim realArguments As String = arguments.Replace("[outputfile]", "-")
            LogInformation("EncoderCheck", "running " & CodecName(codec) & ": " & exePath & " " & realArguments)
            Try
                Dim testAudio() As Byte = BuildTestWave()
                Dim stderrText As String = Nothing
                Dim childExitCode As Integer = 0
                Dim produced As Long = RunEncoder(exePath, realArguments, testAudio, stderrText, childExitCode)
                result.ExitCode = childExitCode
                result.BytesProduced = produced
                result.Detail = FirstMeaningfulLine(stderrText)
                If produced > 0 Then
                    result.Status = Status.Ok
                ElseIf ProducesToFile(exePath, arguments, testAudio) Then
                    ' Nothing on the pipe, but it writes the format perfectly well to a file - so
                    ' the encoder is healthy and there is nothing here for the user to fix. That
                    ' our streaming path cannot pipe every container is a separate problem, ours,
                    ' and does not belong in a verdict about their encoder.
                    result.Status = Status.Ok
                    result.Detail = Nothing
                Else
                    If IsCrashExitCode(childExitCode) Then
                        ' Windows exception codes are 0xC0000005 and friends - the encoder did not
                        ' decline the job, it died doing it. Worth saying differently: "produced
                        ' nothing" reads like a configuration problem, a crash reads like a bug.
                        result.Status = Status.Crashed
                    Else
                        result.Status = Status.ProducedNothing
                    End If
                End If
                LogInformation("EncoderCheck", CodecName(codec) & " -> " & result.Status.ToString() &
                               ", bytes=" & produced & ", exit=" & result.ExitCode &
                               If(String.IsNullOrEmpty(result.Detail), "", ", said: " & result.Detail))
            Catch ex As Exception
                result.Status = Status.TestFailed
                result.Detail = ex.Message
                LogError(ex, "EncoderCheck", "codec=" & codec.ToString())
            End Try
            Return result
        End Function

        ''' <summary>
        ''' Same encoder, same arguments, but writing to a real file instead of the pipe - which is
        ''' what MusicBee's own file converter does. If this works while the pipe run produced
        ''' nothing, the encoder is healthy and the format simply cannot be streamed: its container
        ''' has to seek back to finish its header (MP4 does; MP3, Ogg and FLAC do not).
        ''' Nothing here is codec-specific, so a future format with the same shape is caught too.
        ''' </summary>
        Private Shared Function ProducesToFile(exePath As String, arguments As String, audio() As Byte) As Boolean
            Dim tempPath As String = Path.Combine(Path.GetTempPath(),
                                                  "yaiol-enctest-" & Guid.NewGuid().ToString("N") & ".tmp")
            Try
                Dim stderrText As String = Nothing
                Dim exitCode As Integer = 0
                RunEncoder(exePath, arguments.Replace("[outputfile]", """" & tempPath & """"),
                           audio, stderrText, exitCode)
                Dim written As Long = If(File.Exists(tempPath), New FileInfo(tempPath).Length, 0L)
                LogInformation("EncoderCheck", "retry to a file wrote " & written & " bytes, exit=" & exitCode)
                Return written > 0
            Catch ex As Exception
                LogError(ex, "EncoderCheck.ProducesToFile")
                Return False
            Finally
                Try
                    If File.Exists(tempPath) Then File.Delete(tempPath)
                Catch
                End Try
            End Try
        End Function

        ''' <summary>
        ''' Splits `"C:\dir\enc.exe" -q 5 - -o [outputfile]` into path and arguments. MusicBee
        ''' quotes the executable, but accept an unquoted one too rather than fail the whole check.
        ''' </summary>
        Private Shared Function TrySplitCommandLine(commandLine As String, ByRef exePath As String, ByRef arguments As String) As Boolean
            Dim text As String = commandLine.TrimStart()
            If text.Length = 0 Then Return False
            If text.Chars(0) = """"c Then
                Dim closing As Integer = text.IndexOf(""""c, 1)
                If closing = -1 Then Return False
                exePath = text.Substring(1, closing - 1)
                arguments = text.Substring(closing + 1).Trim()
            Else
                Dim space As Integer = text.IndexOf(" "c)
                If space = -1 Then
                    exePath = text
                    arguments = ""
                Else
                    exePath = text.Substring(0, space)
                    arguments = text.Substring(space + 1).Trim()
                End If
            End If
            Return exePath.Length > 0
        End Function

        ''' <summary>
        ''' Half a second of 44.1kHz 16-bit stereo tone, as a RIFF/WAV - which is the shape BASS
        ''' pipes into these encoders. A tone rather than silence: a silent input can compress to
        ''' almost nothing and muddy the "did it produce anything" test.
        ''' </summary>
        Private Shared Function BuildTestWave() As Byte()
            Const rate As Integer = 44100
            Const channels As Integer = 2
            Dim frames As Integer = rate \ 2
            Dim dataLength As Integer = frames * channels * 2
            Using buffer As New MemoryStream(dataLength + 44), writer As New BinaryWriter(buffer)
                writer.Write(Encoding.ASCII.GetBytes("RIFF"))
                writer.Write(CInt(36 + dataLength))
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "))
                writer.Write(CInt(16))                              ' fmt chunk size
                writer.Write(CShort(1))                             ' PCM
                writer.Write(CShort(channels))
                writer.Write(CInt(rate))
                writer.Write(CInt(rate * channels * 2))             ' byte rate
                writer.Write(CShort(channels * 2))                  ' block align
                writer.Write(CShort(16))                            ' bits per sample
                writer.Write(Encoding.ASCII.GetBytes("data"))
                writer.Write(CInt(dataLength))
                For index As Integer = 0 To frames - 1
                    Dim sample As Short = CShort(6000 * Math.Sin(2.0 * Math.PI * 440.0 * index / rate))
                    For channel As Integer = 1 To channels
                        writer.Write(sample)
                    Next channel
                Next index
                writer.Flush()
                Return buffer.ToArray()
            End Using
        End Function

        ''' <summary>
        ''' Launches the encoder, feeds it the audio and returns how many bytes it wrote to stdout.
        ''' stdout and stderr are drained on their own threads: a child that fills either pipe
        ''' while we are still writing stdin would deadlock otherwise.
        ''' </summary>
        Private Shared Function RunEncoder(exePath As String, arguments As String, audio() As Byte, ByRef stderrText As String, ByRef exitCode As Integer) As Long
            Dim info As New ProcessStartInfo(exePath, arguments) With {
                .UseShellExecute = False,
                .RedirectStandardInput = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True,
                .WorkingDirectory = Path.GetDirectoryName(exePath)
            }
            Dim produced As Long = 0
            Dim errorBuilder As New StringBuilder()
            ' A misconfigured encoder can crash rather than exit, and Windows then puts up its
            ' "X has stopped working" box - over a diagnostic the user ran deliberately, about a
            ' program they did not launch. Child processes inherit the error mode, so set it for
            ' the duration of the launch and put it straight back.
            Dim previousErrorMode As UInteger = SetErrorMode(SEM_FAILCRITICALERRORS Or SEM_NOGPFAULTERRORBOX)
            Dim encoderProcess As Process
            Try
                encoderProcess = Process.Start(info)
            Finally
                SetErrorMode(previousErrorMode)
            End Try
            Using encoderProcess
                Dim stdoutReader As New Thread(
                    Sub()
                        Try
                            Dim chunk(65535) As Byte
                            Dim read As Integer
                            Do
                                read = encoderProcess.StandardOutput.BaseStream.Read(chunk, 0, chunk.Length)
                                produced += read
                            Loop While read > 0
                        Catch
                        End Try
                    End Sub) With {.IsBackground = True}
                Dim stderrReader As New Thread(
                    Sub()
                        Try
                            errorBuilder.Append(encoderProcess.StandardError.ReadToEnd())
                        Catch
                        End Try
                    End Sub) With {.IsBackground = True}
                stdoutReader.Start()
                stderrReader.Start()
                Try
                    encoderProcess.StandardInput.BaseStream.Write(audio, 0, audio.Length)
                    encoderProcess.StandardInput.BaseStream.Flush()
                    LogInformation("EncoderCheck", "wrote " & audio.Length & " bytes to stdin")
                Catch ex As Exception
                    ' An encoder that rejects its arguments exits before reading stdin, so the write
                    ' fails with a broken pipe - a result rather than an error. Say which it was:
                    ' swallowing this silently hid the fact that the encoder got nothing at all.
                    LogInformation("EncoderCheck", "stdin write failed after " & audio.Length &
                                   " bytes offered: " & ex.Message)
                End Try
                Try
                    encoderProcess.StandardInput.Close()
                Catch
                End Try
                If Not encoderProcess.WaitForExit(20000) Then
                    Try
                        encoderProcess.Kill()
                    Catch
                    End Try
                    stderrText = errorBuilder.ToString()
                    Throw New TimeoutException("encoder did not finish within 20 seconds")
                End If
                stdoutReader.Join(5000)
                stderrReader.Join(5000)
                Try
                    exitCode = encoderProcess.ExitCode
                Catch
                End Try
            End Using
            stderrText = errorBuilder.ToString()
            Return produced
        End Function

        Private Const SEM_FAILCRITICALERRORS As UInteger = &H1UI
        Private Const SEM_NOGPFAULTERRORBOX As UInteger = &H2UI

        <Runtime.InteropServices.DllImport("kernel32.dll")>
        Private Shared Function SetErrorMode(uMode As UInteger) As UInteger
        End Function

        ''' <summary>
        ''' Encoders print banners and progress bars; the useful line is the one naming a problem.
        ''' Prefer an ERROR/WARNING/invalid line, else fall back to the last non-empty one.
        ''' </summary>
        Private Shared Function FirstMeaningfulLine(text As String) As String
            If String.IsNullOrEmpty(text) Then Return Nothing
            Dim lines() As String = text.Split(New Char() {ChrW(13), ChrW(10)}, StringSplitOptions.RemoveEmptyEntries)
            For Each line As String In lines
                Dim trimmed As String = line.Trim()
                If trimmed.Length = 0 Then Continue For
                If trimmed.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) <> -1 _
                    OrElse trimmed.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) <> -1 _
                    OrElse trimmed.IndexOf("unknown option", StringComparison.OrdinalIgnoreCase) <> -1 Then
                    Return trimmed
                End If
            Next line
            For index As Integer = lines.Length - 1 To 0 Step -1
                Dim trimmed As String = lines(index).Trim().Trim("*"c).Trim()
                If trimmed.Length > 0 Then Return trimmed
            Next index
            Return Nothing
        End Function

        ''' <summary>
        ''' A Windows exception code rather than a program's own exit status: the top byte is 0xC0
        ''' (STATUS_ACCESS_VIOLATION and friends). Distinguishes "the encoder crashed" from "the
        ''' encoder chose to write nothing".
        ''' </summary>
        Private Shared Function IsCrashExitCode(exitCode As Integer) As Boolean
            Return (CUInt(exitCode) And &HF0000000UI) = &HC0000000UI
        End Function

        ''' <summary>Human-readable name for an output format, matching the settings dropdown.</summary>
        Friend Shared Function CodecName(codec As FileCodec) As String
            Select Case codec
                Case FileCodec.Pcm, FileCodec.AnyPcm : Return "PCM / Wave"
                Case FileCodec.Mp3 : Return "MP3"
                Case FileCodec.Aac : Return "AAC"
                Case FileCodec.Ogg : Return "Ogg"
                Case FileCodec.Flac : Return "FLAC"
                Case Else : Return codec.ToString()
            End Select
        End Function

        ''' <summary>
        ''' One line per format for the dialog. Localised via the L() keys EncTest*, because every
        ''' user-visible string in this plugin is.
        ''' </summary>
        Friend Shared Function Describe(result As Result) As String
            Dim name As String = CodecName(result.Codec)
            Select Case result.Status
                Case Status.Builtin
                    Return String.Format(L("EncTestBuiltin"), name)
                Case Status.Ok
                    Return String.Format(L("EncTestOk"), name)
                Case Status.NoConverter
                    Return String.Format(L("EncTestNoConverter"), name)
                Case Status.ExecutableMissing
                    Return String.Format(L("EncTestExeMissing"), name, result.Detail)
                Case Status.ProducedNothing
                    Return String.Format(L("EncTestNoOutput"), name,
                                         If(String.IsNullOrEmpty(result.Detail), "-", result.Detail))
                Case Status.Crashed
                    Return String.Format(L("EncTestCrashed"), name, "0x" & result.ExitCode.ToString("X8"))
                Case Else
                    Return String.Format(L("EncTestFailed"), name,
                                         If(String.IsNullOrEmpty(result.Detail), "-", result.Detail))
            End Select
        End Function
    End Class
End Class
