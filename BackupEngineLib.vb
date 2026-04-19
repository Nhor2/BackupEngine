Imports System.IO
Imports System.Net.WebRequestMethods
Imports System.Runtime.InteropServices
Imports System.Text.Json
Imports Microsoft.Win32.SafeHandles

Public Class BackupEngine

    Public Enum RestoreMode
        Overwrite
        Merge
        Verify
        DryRun
    End Enum


    ' Eventi Pubblici per notifiche
    Public Event OnProgress(percent As Integer)
    Public Event OnMessage(msg As String)

    ' VSS - Shadow Copy
    Public Property UseVss As Boolean = False


    <DllImport("kernel32.dll", SetLastError:=True, CharSet:=CharSet.Unicode)>
    Public Shared Function CopyFile(lpExistingFileName As String, lpNewFileName As String, bFailIfExists As Boolean) As Boolean
    End Function

    <DllImport("kernel32.dll", SetLastError:=True, CharSet:=CharSet.Unicode, EntryPoint:="CopyFileW")>
    Public Shared Function CopyFileWin32(
        lpExistingFileName As String,
        lpNewFileName As String,
        bFailIfExists As Boolean
    ) As Boolean
    End Function


    ' ================= BACKUP =================
    Public Function RunBackup(sourcePath As String, destRoot As String) As String

        If Not Directory.Exists(sourcePath) Then
            Throw New Exception("Cartella sorgente non trovata")
        End If

        ' Versioning
        Dim timestamp As String = DateTime.Now.ToString("yyyy-MM-dd_HHmmss")
        Dim sourceFolderName As String = New DirectoryInfo(sourcePath).Name
        Dim destPath As String = Path.Combine(destRoot, $"{timestamp}_{sourceFolderName}")

        RaiseEvent OnMessage("Inizio backup...")

        ' Copia
        Dim totalFiles As Integer = 0
        For Each f In Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories)
            totalFiles += 1
        Next
        Dim copied As Integer = 0

        CopyDirectoryWithDatesSafe(sourcePath, destPath, totalFiles, copied)

        ' Manifest
        Dim manifestPath = Path.Combine(destPath, "BackupManifest.json")
        CreateBackupManifest(sourcePath, destPath, manifestPath)

        RaiseEvent OnMessage("Backup completato")

        Return manifestPath
    End Function

    Private Function SafeGetFileSize(path As String) As Long
        Try
            ' Tentativo normale
            Return New FileInfo(path).Length
        Catch
            Try
                ' Fallback long path
                Dim lp = "\\?\" & System.IO.Path.GetFullPath(path)
                Using fs As New FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Return fs.Length
                End Using
            Catch
                Return -1 ' errore
            End Try
        End Try
    End Function


    Private Function ShouldSkipFile(path As String) As Boolean
        Dim name = System.IO.Path.GetFileName(path).ToLowerInvariant()

        ' =========================
        ' TEMP / LOCK
        ' =========================
        If name.EndsWith(".lock") Then Return True
        If name.EndsWith(".tmp") Then Return True
        If name.EndsWith(".temp") Then Return True

        ' =========================
        ' FILE TEMPORANEI OFFICE
        ' =========================
        If name.StartsWith("~$") Then Return True

        ' =========================
        ' FILE DI SISTEMA / CACHE
        ' =========================
        If name = "thumbs.db" Then Return True
        If name = "desktop.ini" Then Return True

        ' =========================
        ' FILE PARZIALI / DOWNLOAD
        ' =========================
        If name.EndsWith(".partial") Then Return True
        If name.EndsWith(".crdownload") Then Return True

        Return False
    End Function



    Public Sub BackupSimulation(sourceDir As String, destDir As String, totalFiles As Integer, ByRef filesCopied As Integer)
        ' Simulazione senza copia

        ' Crea la directory corrente
        RaiseEvent OnMessage("[DRY]: Creo Destinazione: " & destDir)

        For Each file As String In Directory.GetFiles(sourceDir)

            ' Skip file temporanei, downloads parziali etc. Il tempo non aspetta :D
            If ShouldSkipFile(file) Then
                RaiseEvent OnMessage(vbCrLf & "[DRY} SKIP TEMP: " & file)
                Continue For
            End If

            ' Controllo file esistenza (NO long path qui)
            Dim sourcePath As String = "\\?\" & Path.GetFullPath(file)

            ' Long path origine
            Dim longPathOrigin As String = sourcePath

            ' Percorso relativo
            Dim relativePath As String = GetRelativePath(sourceDir, file)

            ' Destinazione
            Dim targetFile As String = Path.Combine(destDir, relativePath)
            Dim targetDirOnly As String = Path.GetDirectoryName(targetFile)

            ' Long path destinazione
            Dim useLongPath As Boolean = (file.Length > 240 OrElse targetFile.Length > 240)

            Dim destPath As String = "\\?\" & Path.GetFullPath(targetFile)

            RaiseEvent OnMessage(vbCrLf & "[DRY] Creazione Cartella: " & targetDirOnly)

            ' Size sicura
            Dim size As Long = 0
            Try
                size = SafeGetFileSize(sourcePath)
                If size = 0 Then
                    RaiseEvent OnMessage(vbCrLf & "[DRY] ZERO byte [" & size.ToString() & "] per " & file)
                End If
            Catch
                RaiseEvent OnMessage(vbCrLf & "[DRY} Errore Size: " & file)
            End Try

            ' Debug path lunghi
            If file.Length > 250 Then
                RaiseEvent OnMessage(vbCrLf & "[DRY] PATH > 250 → " & file)
            End If

            Dim copied As Boolean = False
            Dim normalizedDest As String = Path.GetFullPath(destPath)

            Dim tempPath As String = normalizedDest & ".partial"
            Dim normalizedSource As String = Path.GetFullPath(sourcePath)
            Dim normalizedTemp As String = Path.GetFullPath(tempPath)

            ' Assicurati che la cartella destinazione esista
            Dim destDirOnly As String = Path.GetDirectoryName(destPath)
            RaiseEvent OnMessage(vbCrLf & "[DRY] Creazione cartella " & destDirOnly)

            ' Copia Simulata
            RaiseEvent OnMessage(vbCrLf & "[DRY] COPIED tmp: " & normalizedTemp)
            copied = True

            ' Rinomina atomica
            If copied Then
                RaiseEvent OnMessage(vbCrLf & "[DRY] MOVE atomico: " & normalizedDest)
            End If

            Dim attrs = System.IO.File.GetAttributes(sourcePath)
            RaiseEvent OnMessage(vbCrLf & "[DRY] Attributi: " & file)
            RaiseEvent OnMessage(vbCrLf & "[DRY] SET Attributi: " & file)

            ' Date
            Try
                RaiseEvent OnMessage(vbCrLf & "[DRY] SET Attributi: " & System.IO.File.GetCreationTime(sourcePath) & " " & destPath)
                RaiseEvent OnMessage(vbCrLf & "[DRY] SET Attributi: " & System.IO.File.GetLastWriteTime(sourcePath) & " " & destPath)
                RaiseEvent OnMessage(vbCrLf & "[DRY] SET Attributi: " & System.IO.File.GetLastAccessTime(sourcePath) & " " & destPath)
            Catch
                RaiseEvent OnMessage(vbCrLf & "[DRY] ERRORE date: " & targetFile)
            End Try

            Dim zoneIdentifier As String = destPath & ":Zone.Identifier"
            RaiseEvent OnMessage(vbCrLf & "[DRY] ZoneIdentifier: " & zoneIdentifier)

            ' Attrtibuti Speciali
            Dim attrsExtended As FileAttributes = 0
            Try
                attrsExtended = System.IO.File.GetAttributes(sourcePath)
            Catch
                attrsExtended = 0
            End Try
            ' Compressione
            RaiseEvent OnMessage(vbCrLf & "[DRY] SET Flag Compresso: " & destPath)
            ' Cifratura
            RaiseEvent OnMessage(vbCrLf & "[DRY] SET Flag Encrypted: " & destPath)

            ' =========================
            ' PROGRESS
            ' =========================
            filesCopied += 1
            If totalFiles > 0 Then
                Dim percent As Integer = CInt(filesCopied * 100 / totalFiles)
                RaiseEvent OnProgress(percent)
            End If
            ' Stampa ogni 1000 file
            If filesCopied Mod 1500 = 0 OrElse filesCopied = totalFiles Then
                Dim percent As Double = (filesCopied / totalFiles) * 100
                RaiseEvent OnMessage(String.Format(Environment.NewLine & "[DRY] COPYING >>>> {0} / {1} ({2:0.00}%)" & vbCrLf, filesCopied, totalFiles, percent))
            End If

            For Each dir As String In Directory.GetDirectories(sourceDir)
                Try
                    Dim dirName As String = Path.GetFileName(dir)
                    Dim targetDir As String = Path.Combine(destDir, dirName)

                    BackupSimulation(dir, targetDir, totalFiles, filesCopied)

                Catch ex As UnauthorizedAccessException
                    RaiseEvent OnMessage(vbCrLf & "[DRY] ERRORE Accesso negato DIR: " & dir)
                Catch ex As Exception
                    RaiseEvent OnMessage(vbCrLf & "[DRY] ERRORE DIR: " & dir & " - " & ex.Message)
                End Try
            Next

        Next
    End Sub



    Public Sub CopyDirectoryWithDatesSafe(sourceDir As String, destDir As String, totalFiles As Integer, ByRef filesCopied As Integer)
        Try
            ' Crea la directory corrente
            If Not Directory.Exists(destDir) Then
                Directory.CreateDirectory(destDir)
            End If

            ' =========================
            ' COPIA FILE (solo livello corrente)
            ' =========================
            For Each file As String In Directory.GetFiles(sourceDir)

                ' Skip file temporanei, downloads parziali etc. Il tempo non aspetta :D
                If ShouldSkipFile(file) Then
                    RaiseEvent OnMessage("SKIP TEMP: " & file)
                    Continue For
                End If

                Try
                    ' Controllo file esistenza (NO long path qui)
                    Dim sourcePath As String = If(file.Length > 240,
                             "\\?\" & Path.GetFullPath(file),
                             Path.GetFullPath(file))

                    Dim exists As Boolean = False

                    Try
                        exists = System.IO.File.Exists(sourcePath)
                    Catch
                        exists = False
                    End Try

                    If Not exists Then
                        ' fallback: prova path normale
                        If System.IO.File.Exists(file) Then
                            sourcePath = Path.GetFullPath(file)
                            exists = True
                        End If
                    End If

                    If Not exists Then
                        RaiseEvent OnMessage(vbCrLf & "File sparito: " & file)
                        Continue For
                    End If

                    ' Long path origine
                    Dim longPathOrigin As String = sourcePath

                    ' Percorso relativo
                    Dim relativePath As String = GetRelativePath(sourceDir, file)

                    ' Destinazione
                    Dim targetFile As String = Path.Combine(destDir, relativePath)
                    Dim targetDirOnly As String = Path.GetDirectoryName(targetFile)

                    ' Long path destinazione
                    Dim useLongPath As Boolean = (file.Length > 240 OrElse targetFile.Length > 240)

                    Dim destPath As String = If(useLongPath,
    "\\?\" & Path.GetFullPath(targetFile),
    Path.GetFullPath(targetFile))

                    If Not Directory.Exists(targetDirOnly) Then
                        Directory.CreateDirectory(targetDirOnly)
                    End If

                    ' =========================
                    ' SKIP FILE SE GIÀ ESISTE
                    ' =========================
                    Try
                        If System.IO.File.Exists(destPath) Then

                            Dim srcInfo As New FileInfo(sourcePath)
                            Dim dstInfo As New FileInfo(destPath)

                            ' confronto base (veloce e sufficiente per 90% casi)
                            If srcInfo.Length = dstInfo.Length AndAlso srcInfo.LastWriteTime <= dstInfo.LastWriteTime Then

                                RaiseEvent OnMessage("SKIP (già presente): " & file)

                                filesCopied += 1
                                If totalFiles > 0 Then
                                    Dim percent As Integer = CInt(filesCopied * 100 / totalFiles)
                                    RaiseEvent OnProgress(percent)
                                End If

                                Continue For
                            End If

                        End If
                    Catch ex As Exception
                        RaiseEvent OnMessage("SKIP CHECK ERROR: " & file)
                    End Try

                    ' =========================
                    ' SIZE SICURO
                    ' =========================
                    Dim size As Long = 0
                    Try
                        size = SafeGetFileSize(sourcePath)
                        If size = 0 Then
                            RaiseEvent OnMessage(vbCrLf & "ZERO byte [" & size.ToString() & "] per " & file)
                        End If
                    Catch
                        RaiseEvent OnMessage(vbCrLf & "Errore Size: " & file)
                    End Try

                    ' Debug path lunghi
                    If file.Length > 250 Then
                        RaiseEvent OnMessage(vbCrLf & "PATH > 250 → " & file)
                    End If

                    ' =========================
                    ' COPIA FILE ROBUSTA LONG PATH
                    ' =========================
                    Dim copied As Boolean = False
                    Dim normalizedDest As String = Path.GetFullPath(destPath)

                    Dim tempPath As String = normalizedDest & ".partial"
                    Dim normalizedSource As String = Path.GetFullPath(sourcePath)
                    Dim normalizedTemp As String = Path.GetFullPath(tempPath)


                    ' Assicurati che la cartella destinazione esista
                    Dim destDirOnly As String = Path.GetDirectoryName(destPath)
                    If Not Directory.Exists(destDirOnly) Then Directory.CreateDirectory(destDirOnly)

                    ' =========================
                    ' RETRY COPYFILEW
                    ' =========================
                    For i As Integer = 1 To 3
                        If BackupEngine.CopyFileWin32(normalizedSource, normalizedTemp, False) Then
                            copied = True
                            Exit For
                        Else
                            Dim err = Marshal.GetLastWin32Error()
                            RaiseEvent OnMessage(vbCrLf & "ERRORE CopyFile API: " & err & " -> " & file)
                            Threading.Thread.Sleep(50)
                        End If
                    Next

                    ' =========================
                    ' FALLBACK FILESTREAM
                    ' =========================
                    If Not copied Then
                        RaiseEvent OnMessage("TRY COPY: " & normalizedSource)
                        RaiseEvent OnMessage("FALLBACK TO: " & normalizedTemp)

                        Try
                            Using src As New FileStream(normalizedSource, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                                Using dst As New FileStream(normalizedTemp, FileMode.Create, FileAccess.Write)
                                    src.CopyTo(dst)
                                End Using
                            End Using
                            copied = True
                            RaiseEvent OnMessage(vbCrLf & "COPY FALLBACK FileStream OK -> " & file)
                        Catch ex As Exception
                            RaiseEvent OnMessage(vbCrLf & "ERRORE FALLBACK FileStream: " & ex.Message & " -> " & file)
                        End Try
                    End If

                    ' =========================
                    ' RINOMINA ATOMICO DEFINITIVO   
                    ' =========================
                    If copied Then
                        Try
                            ' Se esiste già (caso raro), elimina
                            If System.IO.File.Exists(normalizedDest) Then System.IO.File.Delete(normalizedDest)

                            If Not System.IO.File.Exists(normalizedTemp) Then
                                RaiseEvent OnMessage("❌ TEMP MISSING -> " & normalizedTemp)
                                copied = False
                                Exit Try
                            End If

                            System.IO.File.Move(normalizedTemp, normalizedDest)

                            RaiseEvent OnMessage("COPIED -> " & normalizedDest)
                        Catch ex As Exception
                            RaiseEvent OnMessage("ERRORE RENAME: " & ex.Message & " -> " & file)
                            copied = False
                        End Try
                    End If

                    ' =========================
                    ' Se ancora non copiato, segnala
                    ' =========================
                    If Not copied Then
                        RaiseEvent OnMessage(vbCrLf & "ERRORE COPIA DEFINITIVO: " & file)
                        Continue For
                    End If

                    ' =========================
                    ' ATTRIBUTI ORIGINE
                    ' =========================
                    Dim attrs As FileAttributes

                    Try
                        attrs = System.IO.File.GetAttributes(sourcePath)
                    Catch
                        RaiseEvent OnMessage(vbCrLf & "ERRORE attributi: " & file)
                        Continue For
                    End Try

                    ' Se ReadOnly → rimuovilo temporaneamente
                    If (attrs And FileAttributes.ReadOnly) = FileAttributes.ReadOnly Then
                        System.IO.File.SetAttributes(destPath, attrs And Not FileAttributes.ReadOnly)
                    End If

                    ' =========================
                    ' RIMUOVE ZONE IDENTIFIER
                    ' =========================
                    Dim zoneIdentifier As String = destPath & ":Zone.Identifier"
                    Try
                        If System.IO.File.Exists(zoneIdentifier) Then
                            System.IO.File.Delete(zoneIdentifier)
                        End If
                    Catch
                        RaiseEvent OnMessage(vbCrLf & "ERRORE Impossibile rimuovere Zone.Identifier: " & targetFile)
                    End Try

                    ' =========================
                    ' DATE
                    ' =========================
                    Try
                        System.IO.File.SetCreationTime(destPath, System.IO.File.GetCreationTime(sourcePath))
                        System.IO.File.SetLastWriteTime(destPath, System.IO.File.GetLastWriteTime(sourcePath))
                        System.IO.File.SetLastAccessTime(destPath, System.IO.File.GetLastAccessTime(sourcePath))
                    Catch
                        RaiseEvent OnMessage(vbCrLf & "ERRORE date: " & targetFile)
                    End Try

                    ' =========================
                    ' RIPRISTINO ATTRIBUTI
                    ' =========================
                    Try
                        System.IO.File.SetAttributes(destPath, attrs)
                    Catch
                        RaiseEvent OnMessage(vbCrLf & "ERRORE set attributi: " & targetFile)
                    End Try

                    ' =========================
                    ' COMPRESSIONE / CIFRATURA
                    ' =========================
                    Dim attrsExtended As FileAttributes = 0
                    Try
                        attrsExtended = System.IO.File.GetAttributes(sourcePath)
                    Catch
                        attrsExtended = 0
                    End Try
                    ' Compressione
                    Try
                        If IsNtfs(destPath) AndAlso (attrsExtended And FileAttributes.Compressed) <> 0 Then
                            BackupEngine.SetCompressed(destPath, True)
                        End If
                    Catch ex As Exception
                        RaiseEvent OnMessage(vbCrLf & "SKIP Flag Compressione: " & targetFile)
                        RaiseEvent OnMessage(vbCrLf & "FS: " & New DriveInfo(Path.GetPathRoot(destPath)).DriveFormat & vbCrLf)
                    End Try
                    ' Cifratura
                    Try
                        If IsNtfs(destPath) AndAlso (attrsExtended And FileAttributes.Encrypted) <> 0 Then
                            BackupEngine.SetEncrypted(destPath, True)
                        End If
                    Catch ex As Exception
                        RaiseEvent OnMessage(vbCrLf & "SKIP Flag Cifratura: " & targetFile)
                        RaiseEvent OnMessage(vbCrLf & "FS: " & New DriveInfo(Path.GetPathRoot(destPath)).DriveFormat & vbCrLf)
                    End Try



                    ' =========================
                    ' PROGRESS
                    ' =========================
                    filesCopied += 1
                    If totalFiles > 0 Then
                        Dim percent As Integer = CInt(filesCopied * 100 / totalFiles)
                        RaiseEvent OnProgress(percent)
                    End If
                    ' Stampa ogni 1000 file
                    If filesCopied Mod 1500 = 0 OrElse filesCopied = totalFiles Then
                        Dim percent As Double = (filesCopied / totalFiles) * 100
                        RaiseEvent OnMessage(String.Format(Environment.NewLine & "COPYING >>>> {0} / {1} ({2:0.00}%)" & vbCrLf, filesCopied, totalFiles, percent))
                    End If

                Catch ex As UnauthorizedAccessException
                    RaiseEvent OnMessage(vbCrLf & "ERRORE Accesso negato FILE: " & file)
                Catch ex As Exception
                    RaiseEvent OnMessage(vbCrLf & "ERRORE FILE: " & file & " - " & ex.Message)
                End Try
            Next

            ' =========================
            ' RICORSIONE CARTELLE
            ' =========================
            For Each dir As String In Directory.GetDirectories(sourceDir)
                Try
                    Dim dirName As String = Path.GetFileName(dir)
                    Dim targetDir As String = Path.Combine(destDir, dirName)

                    CopyDirectoryWithDatesSafe(dir, targetDir, totalFiles, filesCopied)

                Catch ex As UnauthorizedAccessException
                    RaiseEvent OnMessage(vbCrLf & "ERRORE Accesso negato DIR: " & dir)
                Catch ex As Exception
                    RaiseEvent OnMessage(vbCrLf & "ERRORE DIR: " & dir & " - " & ex.Message)
                End Try
            Next

        Catch ex As UnauthorizedAccessException
            RaiseEvent OnMessage(vbCrLf & "ERRORE Accesso negato DIR principale: " & sourceDir)
        Catch ex As Exception
            RaiseEvent OnMessage(vbCrLf & "ERRORE generale: " & ex.Message)
        End Try
    End Sub


    Private Function IsNtfs(path As String) As Boolean
        Try
            Dim root = System.IO.Path.GetPathRoot(path)
            Dim di As New DriveInfo(root)
            Return di.DriveFormat.ToUpper() = "NTFS"
        Catch
            Return False
        End Try
    End Function


    Private Function GetRelativePath(basePath As String, fullPath As String) As String
        Dim baseFull = Path.GetFullPath(basePath).TrimEnd("\"c) & "\"
        Dim fullFull = Path.GetFullPath(fullPath)

        ' 🔥 rimuove eventuale prefisso \\?\
        If baseFull.StartsWith("\\?\") Then baseFull = baseFull.Substring(4)
        If fullFull.StartsWith("\\?\") Then fullFull = fullFull.Substring(4)

        If fullFull.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase) Then

            Dim rel = fullFull.Substring(baseFull.Length)

            ' 🔥 normalizzazione finale
            Return rel.Replace("/"c, "\"c).ToLowerInvariant()

        End If

        Throw New Exception("Path non sotto base: " & fullFull)
    End Function


    Private Function NormalizePath(path As String) As String
        Dim full = System.IO.Path.GetFullPath(path)

        If Not full.StartsWith("\\?\") Then
            Return "\\?\" & full
        End If

        Return full
    End Function

    Public Sub CreateBackupManifest(sourceDir As String, destDir As String, manifestPath As String)
        Dim manifest As New BackupManifest()
        manifest.BackupDate = DateTime.Now
        manifest.BackupUser = Environment.UserName
        manifest.SourceFolder = sourceDir
        manifest.DestinationFolder = destDir

        Dim totalFiles As Long = 0

        Try
            For Each file As String In Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories)

                ' Skip file temporanei, downloads parziali etc. Il tempo non aspetta :D
                If ShouldSkipFile(file) Then
                    RaiseEvent OnMessage("SKIP TEMP: " & file)
                    Continue For
                End If

                Try
                    totalFiles += 1
                Catch ex As Exception
                    Debug.WriteLine("ERRORE CONTEGGIO FILE: " & file)
                End Try
            Next

        Catch ex As Exception
            Debug.WriteLine("ERRORE ENUMERAZIONE: " & ex.Message)
        End Try

        manifest.TotalFiles = totalFiles

        Dim totalSize As Long = 0

        For Each file In Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories)

            ' Skip file temporanei, downloads parziali etc. Il tempo non aspetta :D
            If ShouldSkipFile(file) Then
                RaiseEvent OnMessage("SKIP TEMP: " & file)
                Continue For
            End If

            Try
                Dim path = NormalizePath(file)
                If path.Length > 250 Then Debug.WriteLine("PATH LEN > 250: " & path.Length.ToString)

                If Not System.IO.File.Exists(path) Then
                    Debug.WriteLine("NOT EXISTS: " & path)
                    Continue For
                End If

                Dim size As Long
                Using fs = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    size = fs.Length
                End Using

                Dim creation = System.IO.File.GetCreationTime(path)
                Dim write = System.IO.File.GetLastWriteTime(path)
                Dim access = System.IO.File.GetLastAccessTime(path)

                Dim hash = CRC.ComputeFullHash(path)

                ' Attributi estesi
                Dim attrsExtended As FileAttributes = 0
                Try
                    attrsExtended = System.IO.File.GetAttributes(path)
                Catch
                    attrsExtended = 0
                End Try
                Dim IsCompressed = (attrsExtended And FileAttributes.Compressed) <> 0
                Dim IsEncrypted = (attrsExtended And FileAttributes.Encrypted) <> 0

                manifest.FileList.Add(New FileInfoEntry With {
            .RelativePath = GetRelativePath(sourceDir, file),
            .FileName = System.IO.Path.GetFileName(file),
            .SizeBytes = size,
            .CreationTime = creation,
            .LastWriteTime = write,
            .LastAccessTime = access,
            .FullHash = hash,
            .IsCompressed = IsCompressed,
            .IsEncrypted = IsEncrypted
        })

            Catch ex As Exception
                Debug.WriteLine("ERRORE FILE: " & file)
                Debug.WriteLine(ex.Message)
            End Try

        Next

        manifest.TotalSizeBytes = totalSize

        ' Scrivi manifest in JSON
        Dim options As New JsonSerializerOptions() With {
        .WriteIndented = True
    }
        Dim jsonString As String = JsonSerializer.Serialize(manifest, options)
        System.IO.File.WriteAllText(manifestPath, jsonString)
    End Sub


    ' Questa classe dopo il Backup contiene la lista dei files corretti, diversi ma copiati, mancanti non copiati
    Public Class CompareResult
        Public Property OkFiles As New List(Of String)
        Public Property DifferentFiles As New List(Of String)
        Public Property MissingFiles As New List(Of String)
    End Class


    Public Function CompareWithManifest(manifestPath As String, crc As CRC) As CompareResult
        Dim result As New CompareResult

        ' Leggi JSON del manifest
        Dim json = System.IO.File.ReadAllText(manifestPath)
        Dim manifest = JsonSerializer.Deserialize(Of BackupManifest)(json)

        For Each file In manifest.FileList
            Dim destPath = NormalizePath(Path.Combine(manifest.DestinationFolder, file.RelativePath))
            Dim sourcePath = NormalizePath(Path.Combine(manifest.SourceFolder, file.RelativePath))

            Console.WriteLine("EXPECTED: " & destPath)
            Console.WriteLine("EXISTS: " & System.IO.File.Exists(destPath))

            ' File mancante
            If Not System.IO.File.Exists(destPath) OrElse Not System.IO.File.Exists(sourcePath) Then
                Console.WriteLine("SRC: " & sourcePath)
                Console.WriteLine("DST: " & destPath)
                Console.WriteLine("EXISTS DST: " & System.IO.File.Exists(destPath))

                result.MissingFiles.Add(file.RelativePath)
                Continue For
            End If

            Dim size As Long
            Dim lastWrite As DateTime

            Using fs = New FileStream(destPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                size = fs.Length
            End Using

            lastWrite = System.IO.File.GetLastWriteTimeUtc(destPath)

            ' Controllo veloce: dimensione + LastWriteTime UTC
            Dim keyManifest = CRC.BuildKey(file.SizeBytes, file.LastWriteTime.ToUniversalTime())
            Dim keyDisk = CRC.BuildKey(size, lastWrite)

            If keyManifest <> keyDisk Then
                result.DifferentFiles.Add(file.RelativePath)
                Continue For
            End If

            ' Controllo CRC completo solo se necessario
            'Dim hashSource = CRC.ComputeFullHash(sourcePath)
            Dim hashSource = file.FullHash
            Dim hashDest = CRC.ComputeFullHash(destPath)

            If hashSource = hashDest Then
                result.OkFiles.Add(file.RelativePath)
            Else
                result.DifferentFiles.Add(file.RelativePath)
            End If
        Next

        Return result
    End Function

    Public Function CountFilesSafe(folder As String) As Integer
        ' conteggio files streaming senza ricorsione per evitare stack overflow su cartelle molto profonde
        Dim count As Integer = 0
        Dim stack As New Stack(Of String)

        stack.Push(folder)

        While stack.Count > 0

            Dim current = stack.Pop()

            Try
                ' File
                For Each file In Directory.EnumerateFiles(current)
                    count += 1
                Next

                ' Directory
                For Each dirs In Directory.EnumerateDirectories(current)
                    stack.Push(dirs)
                Next

            Catch ex As UnauthorizedAccessException
                RaiseEvent OnMessage("Accesso negato: " & current)
            Catch ex As Exception
                RaiseEvent OnMessage("Errore su: " & current & " - " & ex.Message)
            End Try

        End While

        Return count
    End Function

    Public Sub ConfrontaBackup(backups As List(Of String))
        ' Funzione principale per confrontare i backup sul disco, confronto diretto di file
        If backups.Count < 2 Then Return

        Dim refBackup As String = backups(0)

        ' SOLO reference in memoria
        Dim refFiles = Directory.EnumerateFiles(refBackup, "*.*", SearchOption.AllDirectories) _
            .ToDictionary(Function(f) f.Substring(refBackup.Length + 1),
                          Function(f) New FileInfo(f))

        ' Creiamo un CRCAA per le funzioni di controllo completo
        Dim crc As New CRC()

        For i As Integer = 1 To backups.Count - 1

            Dim currBackup = backups(i)

            Dim nuovi As New List(Of String)
            Dim modificati As New List(Of String)

            ' Copia delle chiavi → servirà per trovare cancellati
            Dim remainingRef = New HashSet(Of String)(refFiles.Keys)

            ' 🔹 Streaming sui file correnti
            For Each file In Directory.EnumerateFiles(currBackup, "*.*", SearchOption.AllDirectories)

                Dim relPath = file.Substring(currBackup.Length + 1)
                Dim currFi As New FileInfo(file)

                If refFiles.ContainsKey(relPath) Then

                    Dim refFi = refFiles(relPath)

                    ' trovato → rimuovi dai "cancellati"
                    remainingRef.Remove(relPath)

                    ' confronto veloce
                    If refFi.Length <> currFi.Length OrElse refFi.LastWriteTimeUtc <> currFi.LastWriteTimeUtc Then

                        If CRC.ComputeFullHash(refFi.FullName) <> CRC.ComputeFullHash(currFi.FullName) Then
                            modificati.Add(relPath)
                        End If

                    End If

                Else
                    ' file nuovo
                    nuovi.Add(relPath)
                End If

            Next

            ' quello che resta = cancellati
            Dim cancellati = remainingRef.ToList()

            ' Output
            RaiseEvent OnMessage(Environment.NewLine & $"In {currBackup}:")

            If cancellati.Count > 0 Then RaiseEvent OnMessage("Cancellati: " & String.Join(", ", cancellati))
            If nuovi.Count > 0 Then RaiseEvent OnMessage("Nuovi: " & String.Join(", ", nuovi))
            If modificati.Count > 0 Then RaiseEvent OnMessage("Modificati: " & String.Join(", ", modificati))

            If cancellati.Count = 0 AndAlso nuovi.Count = 0 AndAlso modificati.Count = 0 Then
                RaiseEvent OnMessage("Nessuna differenza.")
            End If

        Next
    End Sub


    Public Function RunRestore(manifestPath As String, Optional targetRoot As String = Nothing, Optional overwrite As Boolean = True) As Integer
        ' Ripristino backup
        If Not System.IO.File.Exists(manifestPath) Then
            Throw New Exception("Manifest non trovato")
        End If

        RaiseEvent OnMessage("Inizio restore...")

        Dim jsonString As String = System.IO.File.ReadAllText(manifestPath)
        Dim manifest As BackupManifest = JsonSerializer.Deserialize(Of BackupManifest)(jsonString)

        ' Se non specificato → usa source originale
        If String.IsNullOrEmpty(targetRoot) Then
            targetRoot = manifest.SourceFolder
        End If

        If Not Directory.Exists(targetRoot) Then
            Directory.CreateDirectory(targetRoot)
        End If

        Dim totalFiles As Integer = manifest.FileList.Count
        Dim processed As Integer = 0
        Dim failed As Integer = 0

        For Each entry In manifest.FileList

            Try
                Dim sourceFile = NormalizePath(Path.Combine(manifest.DestinationFolder, entry.RelativePath))
                Dim destFile = NormalizePath(Path.Combine(targetRoot, entry.RelativePath))

                Dim destDir = Path.GetDirectoryName(destFile)
                If Not Directory.Exists(destDir) Then Directory.CreateDirectory(destDir)


                ' =========================
                ' COPY ROBUSTA (COME BACKUP)
                ' =========================
                Dim tempFile = destFile & ".partial"
                Dim copied As Boolean = False

                ' retry CopyFileW
                For i As Integer = 1 To 3
                    If CopyFileWin32(sourceFile, tempFile, False) Then
                        copied = True
                        Exit For
                    Else
                        Dim err = Marshal.GetLastWin32Error()
                        RaiseEvent OnMessage("ERR CopyFile: " & err & " -> " & destFile)
                        Threading.Thread.Sleep(50)
                    End If
                Next

                ' fallback
                If Not copied Then
                    Try
                        Using src As New FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                            Using dst As New FileStream(tempFile, FileMode.Create, FileAccess.Write)
                                src.CopyTo(dst)
                            End Using
                        End Using
                        copied = True
                    Catch ex As Exception
                        RaiseEvent OnMessage("ERR fallback: " & ex.Message & " -> " & destFile)
                    End Try
                End If

                If Not copied Then
                    failed += 1
                    RaiseEvent OnMessage("FALLITO: " & destFile)
                    GoTo ProgressStep
                End If

                ' rename atomico
                If System.IO.File.Exists(destFile) Then System.IO.File.Delete(destFile)
                System.IO.File.Move(tempFile, destFile)

                ' =========================
                ' DATE
                ' =========================
                System.IO.File.SetCreationTime(destFile, entry.CreationTime)
                System.IO.File.SetLastWriteTime(destFile, entry.LastWriteTime)
                System.IO.File.SetLastAccessTime(destFile, entry.LastAccessTime)

                ' =========================
                ' VERIFICA SIZE
                ' =========================
                Dim sizeSrc = SafeGetFileSize(sourceFile)
                Dim sizeDst = SafeGetFileSize(destFile)

                If sizeSrc <> sizeDst Then
                    RaiseEvent OnMessage("SIZE MISMATCH: " & destFile)
                    failed += 1
                Else
                    RaiseEvent OnMessage("OK: " & destFile)
                End If

                '=========================
                ' ATTRIBUTI NTFS
                ' =========================
                Try
                    If entry.IsCompressed Then
                        SetCompressed(destFile, True)
                    End If
                Catch ex As Exception
                    RaiseEvent OnMessage("COMPRESSION ERROR: " & destFile)
                End Try

                Try
                    If entry.IsEncrypted Then
                        SetEncrypted(destFile, True)
                    End If
                Catch ex As Exception
                    RaiseEvent OnMessage("ENCRYPTION ERROR: " & destFile)
                End Try

            Catch ex As Exception
                failed += 1
                RaiseEvent OnMessage("ERRORE: " & ex.Message)
            End Try

ProgressStep:
            processed += 1
            If totalFiles > 0 Then
                Dim percent As Integer = CInt(processed * 100 / totalFiles)
                RaiseEvent OnProgress(percent)
            End If

            ' Stampa ogni 1000 file
            If processed Mod 1500 = 0 OrElse processed = totalFiles Then
                Dim percent As Double = (processed / totalFiles) * 100
                RaiseEvent OnMessage(String.Format(Environment.NewLine & "COPYING >>>> {0} / {1} ({2:0.00}%)" & vbCrLf, processed, totalFiles, percent))
            End If

        Next

        RaiseEvent OnMessage("Restore completato")

        Return failed
    End Function


    Public Function RunRestoreMode(manifestPath As String, mode As RestoreMode, Optional overwrite As Boolean = True, Optional restoreRoot As String = Nothing) As Integer
        ' Nuovo con modalità Simulazione (Dry-Run), Overwrite, Merge etc.
        Dim json = System.IO.File.ReadAllText(manifestPath)
        Dim manifest = JsonSerializer.Deserialize(Of BackupManifest)(json)

        Dim root As String = If(String.IsNullOrEmpty(restoreRoot), manifest.SourceFolder, restoreRoot)

        ' Contatori
        Dim errors As Integer = 0
        Dim totalFiles As Integer = manifest.FileList.Count
        Dim processed As Integer = 0

        RaiseEvent OnMessage("Restore Mode: " & mode.ToString())

        ' Per tutti i files del manifesto
        For Each entry In manifest.FileList

            Dim targetFile = NormalizePath(Path.Combine(root, entry.RelativePath))
            Dim backupFile = NormalizePath(Path.Combine(manifest.DestinationFolder, entry.RelativePath))

            Select Case mode
                ' Simulazione
                Case RestoreMode.DryRun
                    RaiseEvent OnMessage("[DRY] " & targetFile)

                ' Verifica
                Case RestoreMode.Verify
                    If Not FileExistsSafe(targetFile) Then
                        RaiseEvent OnMessage("MISSING: " & targetFile)
                        errors += 1
                    End If

                ' Sovrascrivi
                Case RestoreMode.Overwrite
                    CopyFileInternal(entry, backupFile, targetFile)


                ' Merge
                Case RestoreMode.Merge
                    If Not FileExistsSafe(targetFile) Then
                        CopyFileInternal(entry, backupFile, targetFile)
                    Else
                        If NeedsUpdate(backupFile, targetFile) Then
                            CopyFileInternal(entry, backupFile, targetFile)
                        Else
                            RaiseEvent OnMessage("SKIP: " & targetFile)
                        End If
                    End If

            End Select

            ' Progresso in ogni modalità
            processed += 1
            If totalFiles > 0 Then
                Dim percent As Integer = CInt(processed * 100 / totalFiles)
                RaiseEvent OnProgress(percent)
            End If

            ' Stampa ogni 1000 file
            If processed Mod 1500 = 0 OrElse processed = totalFiles Then
                Dim percent As Double = (processed / totalFiles) * 100
                RaiseEvent OnMessage(String.Format(Environment.NewLine & "COPYING >>>> {0} / {1} ({2:0.00}%)" & vbCrLf, processed, totalFiles, percent))
            End If

        Next

        RaiseEvent OnMessage("Restore " & mode.ToString() & " completato " & Now.ToString)

        Return errors
    End Function

    Private Function FileExistsSafe(path As String) As Boolean
        Try
            Dim attr As Integer = GetFileAttributesW(path)
            Return attr <> INVALID_FILE_ATTRIBUTES
        Catch
            Return False
        End Try
    End Function


    Private Function NeedsUpdate(src As String, dst As String) As Boolean
        ' Aggiorniamo il file solo quando è necessario
        Dim sizeSrc = SafeGetFileSize(src)
        If sizeSrc <= 0 Then
            Return True ' se sorgente non leggibile → forziamo update
        End If

        Dim sizeDst = SafeGetFileSize(dst)

        ' se destinazione non esiste → update
        If sizeDst <= 0 Then Return True

        ' fast path: size diverso
        If sizeSrc <> sizeDst Then Return True

        ' superfast hash solo se size uguale
        Dim h1 = CRC.ComputeSuperFastHash(src)
        Dim h2 = CRC.ComputeSuperFastHash(dst)

        Return h1 <> h2
    End Function


    Private Sub CopyFileInternal(entry As FileInfoEntry, sourceFile As String, destFile As String)
        ' =========================
        ' COPY ROBUSTA (COME BACKUP)
        ' =========================
        Dim tempFile = destFile & ".partial"
        Dim copied As Boolean = False

        ' retry CopyFileW
        For i As Integer = 1 To 3
            If CopyFileWin32(sourceFile, tempFile, False) Then
                copied = True
                Exit For
            Else
                Dim err = Marshal.GetLastWin32Error()
                RaiseEvent OnMessage("ERR CopyFile: " & err & " -> " & destFile)
                Threading.Thread.Sleep(50)
            End If
        Next

        ' fallback
        If Not copied Then
            Try
                Using src As New FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Using dst As New FileStream(tempFile, FileMode.Create, FileAccess.Write)
                        src.CopyTo(dst)
                    End Using
                End Using
                copied = True
            Catch ex As Exception
                RaiseEvent OnMessage("ERR fallback: " & ex.Message & " -> " & destFile)
            End Try
        End If

        If Not copied Then
            RaiseEvent OnMessage("FALLITO: " & destFile)
        End If

        ' rename atomico
        If System.IO.File.Exists(destFile) Then System.IO.File.Delete(destFile)
        System.IO.File.Move(tempFile, destFile)

        ' =========================
        ' DATE
        ' =========================
        System.IO.File.SetCreationTime(destFile, entry.CreationTime)
        System.IO.File.SetLastWriteTime(destFile, entry.LastWriteTime)
        System.IO.File.SetLastAccessTime(destFile, entry.LastAccessTime)

        ' =========================
        ' VERIFICA SIZE
        ' =========================
        Dim sizeSrc = SafeGetFileSize(sourceFile)
        Dim sizeDst = SafeGetFileSize(destFile)

        If sizeSrc <> sizeDst Then
            RaiseEvent OnMessage("SIZE MISMATCH: " & destFile)
        Else
            RaiseEvent OnMessage("OK: " & destFile)
        End If

        '=========================
        ' ATTRIBUTI NTFS
        ' =========================
        Try
            If entry.IsCompressed Then
                SetCompressed(destFile, True)
            End If
        Catch ex As Exception
            RaiseEvent OnMessage("COMPRESSION ERROR: " & destFile)
        End Try

        Try
            If entry.IsEncrypted Then
                SetEncrypted(destFile, True)
            End If
        Catch ex As Exception
            RaiseEvent OnMessage("ENCRYPTION ERROR: " & destFile)
        End Try
    End Sub

    Private Const INVALID_FILE_ATTRIBUTES As Integer = -1

    <DllImport("kernel32.dll", SetLastError:=True, CharSet:=CharSet.Unicode)>
    Public Shared Function GetFileAttributesW(lpFileName As String) As Integer
    End Function


    Public Shared Sub SetCompressed(filePath As String, compress As Boolean)
        ' Aggiunge il flag "Compresso" al file di backup se l'originale lo è.
        Dim handle As SafeFileHandle = CreateFile(filePath,
                                                 GENERIC_READ_WRITE, ' GENERIC_READ Or GENERIC_WRITE
                                                 3,          ' FILE_SHARE_READ Or FILE_SHARE_WRITE
                                                 IntPtr.Zero,
                                                 3,          ' OPEN_EXISTING
                                                 0,
                                                 IntPtr.Zero)

        If handle.IsInvalid Then Throw New System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())

        Dim level As UShort = If(compress, COMPRESSION_FORMAT_DEFAULT, COMPRESSION_FORMAT_NONE)
        Dim bytesReturned As UInteger
        If Not DeviceIoControl(handle, FSCTL_SET_COMPRESSION, level, 2, IntPtr.Zero, 0, bytesReturned, IntPtr.Zero) Then
            Throw New System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())
        End If
        handle.Close()
    End Sub

    Public Shared Sub SetEncrypted(filePath As String, encrypt As Boolean)
        ' Se legge il flag "Crittografato" vuole dire che stai copiano un file da EFS. Encrypted File System
        Dim success As Boolean
        If encrypt Then
            success = EncryptFile(filePath)
        Else
            success = DecryptFile(filePath, 0)
        End If

        If Not success Then Throw New System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error())
    End Sub
End Class

Public Class BackupManifest
    Public Property BackupDate As DateTime            ' Data del backup
    Public Property BackupUser As String             ' Utente che ha eseguito
    Public Property SourceFolder As String           ' Cartella di origine
    Public Property DestinationFolder As String      ' Cartella di destinazione
    Public Property TotalFiles As Integer            ' Numero totale di file copiati
    Public Property TotalSizeBytes As Long           ' Dimensione totale dei file
    Public Property FileList As List(Of FileInfoEntry) ' Lista dettagli file

    Public Sub New()
        FileList = New List(Of FileInfoEntry)
    End Sub
End Class

Public Class FileInfoEntry
    Public Property RelativePath As String          ' Percorso relativo dal source
    Public Property FileName As String              ' Nome file
    Public Property SizeBytes As Long               ' Dimensione
    Public Property CreationTime As DateTime        ' Data creazione
    Public Property LastWriteTime As DateTime       ' Ultima modifica
    Public Property LastAccessTime As DateTime      ' Ultimo accesso
    Public Property FullHash As String              ' CRC

    ' 🔥 NUOVI FLAG NTFS
    Public Property IsCompressed As Boolean
    Public Property IsEncrypted As Boolean
End Class
