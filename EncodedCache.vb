Imports System.IO
Imports System.Threading

Partial Public Class Plugin

    ''' <summary>
    ''' Keeps the encoded file around so one play does not encode the same track several times.
    '''
    ''' A single play produces three or four requests - the control point probes it, ffprobe reads
    ''' its metadata, then the real player fetches it - and without this each of them encodes the
    ''' whole track from scratch. Measured on one AAC track: four encodes of 3.5 seconds each,
    ''' about fourteen seconds of work to play one song, for four byte-identical results.
    '''
    ''' Requests that arrive while an encode is already running wait for it rather than starting
    ''' their own, which is what turns those four encodes into one.
    ''' </summary>
    Friend NotInheritable Class EncodedCache

        Private NotInheritable Class Entry
            Public ReadOnly Gate As New Object()
            Public Path As String
            Public Length As Long
            Public LastUsedTicks As Long
        End Class

        ''' <summary>
        ''' The real limit is BYTES, not entries. A count limit is meaningless here because the
        ''' things being counted differ by two orders of magnitude: four pop songs are about 60 MB,
        ''' four hour-long podcasts are 1.2 GB. Measured on a real library - one hour of FLAC came
        ''' out at 298 MB - so a count of four quietly parked nearly a gigabyte in TEMP.
        ''' 512 MB holds one long track plus whatever is being prefetched, or a dozen songs.
        ''' </summary>
        Private Const MaxBytes As Long = 512L * 1024L * 1024L

        ''' <summary>Secondary guard, so a pathological stream of tiny files cannot grow the table.</summary>
        Private Const MaxEntries As Integer = 8
        Private Shared ReadOnly MaxAge As TimeSpan = TimeSpan.FromMinutes(15)

        Private Shared ReadOnly entries As New Dictionary(Of String, Entry)(StringComparer.Ordinal)
        Private Shared ReadOnly tableLock As New Object()

        ''' <summary>
        ''' Our own folder under TEMP, so a sweep can be sure everything it deletes is ours and a
        ''' crash cannot leave files somewhere nobody thinks to look.
        ''' </summary>
        Private Shared ReadOnly cacheFolder As String =
            Path.Combine(Path.GetTempPath(), "yaiol-upnp-encoded")

        Shared Sub New()
            ' Anything already here is from a previous run that did not get to clean up - MusicBee
            ' was killed, or the machine went down mid-stream. Nothing can still be using it.
            Try
                If Directory.Exists(cacheFolder) Then
                    For Each leftover As String In Directory.GetFiles(cacheFolder)
                        Try
                            File.Delete(leftover)
                        Catch
                        End Try
                    Next leftover
                Else
                    Directory.CreateDirectory(cacheFolder)
                End If
            Catch ex As Exception
                LogError(ex, "EncodedCache.Sweep")
            End Try
        End Sub

        ''' <summary>
        ''' Returns a file holding the encoded track, producing it only if we do not already have
        ''' one. <paramref name="encodeTo"/> is handed a path to write; it is called at most once
        ''' per key, with every other caller for that key waiting on the result.
        ''' Returns Nothing when the encode produced nothing.
        ''' </summary>
        Friend Shared Function GetOrCreate(key As String, logId As String, encodeTo As Action(Of String)) As String
            Dim entry As Entry = Nothing
            SyncLock tableLock
                If Not entries.TryGetValue(key, entry) Then
                    entry = New Entry()
                    entries(key) = entry
                End If
            End SyncLock

            ' Per-entry lock, not the table lock: a slow encode must not block requests for other
            ' tracks, but two requests for the SAME track have to queue or we gain nothing.
            SyncLock entry.Gate
                If entry.Path IsNot Nothing AndAlso File.Exists(entry.Path) Then
                    entry.LastUsedTicks = DateTime.UtcNow.Ticks
                    LogInformation(logId, "reusing cached encode (" & New FileInfo(entry.Path).Length & " bytes)")
                    Return entry.Path
                End If

                Dim target As String = Path.Combine(cacheFolder, Guid.NewGuid().ToString("N") & ".tmp")
                Try
                    Directory.CreateDirectory(cacheFolder)
                    encodeTo(target)
                Catch ex As Exception
                    LogError(ex, "EncodedCache.Encode", "key=" & key)
                    Try
                        If File.Exists(target) Then File.Delete(target)
                    Catch
                    End Try
                    Return Nothing
                End Try

                If Not File.Exists(target) OrElse New FileInfo(target).Length <= 0 Then
                    Try
                        If File.Exists(target) Then File.Delete(target)
                    Catch
                    End Try
                    Return Nothing
                End If

                entry.Path = target
                entry.Length = New FileInfo(target).Length
                entry.LastUsedTicks = DateTime.UtcNow.Ticks
            End SyncLock

            Prune()
            Return entry.Path
        End Function

        ''' <summary>
        ''' Drop entries past the age limit, then the oldest until the cache is back under its byte
        ''' budget (and, as a backstop, its entry count). Deleting a file that is still being sent is
        ''' safe: the serving stream opens it with FILE_SHARE_DELETE, so Windows simply removes the
        ''' name now and frees the data once the last handle closes.
        ''' </summary>
        Private Shared Sub Prune()
            Dim doomed As New List(Of String)
            SyncLock tableLock
                Dim cutoff As Long = DateTime.UtcNow.Subtract(MaxAge).Ticks
                For Each key As String In entries.Keys.ToList()
                    Dim entry As Entry = entries(key)
                    If entry.Path IsNot Nothing AndAlso entry.LastUsedTicks < cutoff Then
                        doomed.Add(entry.Path)
                        entries.Remove(key)
                    End If
                Next key

                ' Oldest first, and never the most recently used one: that is the track playing
                ' right now, and the request that just created it still has to be served.
                Dim byAge As List(Of KeyValuePair(Of String, Entry)) =
                    entries.OrderBy(Function(p) p.Value.LastUsedTicks).ToList()
                Dim total As Long = byAge.Sum(Function(p) p.Value.Length)
                Dim index As Integer = 0
                While index < byAge.Count - 1 AndAlso (total > MaxBytes OrElse entries.Count > MaxEntries)
                    Dim victim As KeyValuePair(Of String, Entry) = byAge(index)
                    If victim.Value.Path IsNot Nothing Then doomed.Add(victim.Value.Path)
                    total -= victim.Value.Length
                    entries.Remove(victim.Key)
                    index += 1
                End While
            End SyncLock
            For Each path As String In doomed
                Try
                    If File.Exists(path) Then File.Delete(path)
                Catch
                End Try
            Next path
        End Sub

        ''' <summary>Called when the server stops - leave nothing behind on a clean shutdown.</summary>
        Friend Shared Sub Clear()
            Dim doomed As New List(Of String)
            SyncLock tableLock
                For Each entry As Entry In entries.Values
                    If entry.Path IsNot Nothing Then doomed.Add(entry.Path)
                Next entry
                entries.Clear()
            End SyncLock
            For Each path As String In doomed
                Try
                    If File.Exists(path) Then File.Delete(path)
                Catch
                End Try
            Next path
        End Sub
    End Class
End Class
