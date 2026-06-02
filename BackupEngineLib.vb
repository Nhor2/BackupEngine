Imports System.IO
Imports System.IO.Ports
Imports System.Net.WebRequestMethods
Imports System.Runtime.InteropServices
Imports System.Text
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
    Public Event OnFinished()
    Public Event OnStopped()

    ' VSS - Shadow Copy
    Public Property UseVss As Boolean = False

    ' Full Verbose
    Public Property FullVerbose As Boolean = False

    ' Stop Sicuro
    Public Property SecurityStop As Integer = 0
    Public Property SecurityStopFolder As String = ""
    Private stopFile As String = Path.Combine(SecurityStopFolder, "STOPP.safe")

    ' notifica alla form
    Public Event OnYieldRequired()

    ' Skipping aggregato
    Public SkippingFile As Long = 0
    Public SkippingError As Long = 0

    ' Check time per STOP sicuro
    Private lastStopCheck As DateTime = DateTime.MinValue


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
        RaiseEvent OnFinished()

        Return manifestPath
    End Function

    Public Function SafeGetFileSize(path As String) As Long
        If String.IsNullOrWhiteSpace(path) Then Return -1

        Try
            Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Return fs.Length
            End Using
        Catch ex As FileNotFoundException
            Return -2
        Catch ex As DirectoryNotFoundException
            Return -2
        Catch
            ' fallback long path
            Try
                Dim lp = ToLongPath(path)

                Using fs As New FileStream(lp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Return fs.Length
                End Using
            Catch ex2 As FileNotFoundException
                Return -2
            Catch ex2 As DirectoryNotFoundException
                Return -2
            Catch
                Return -1
            End Try
        End Try
    End Function

    Public Function ToLongPath(path As String) As String
        If String.IsNullOrWhiteSpace(path) Then Return path

        If path.StartsWith("\\?\") Then Return path

        If path.StartsWith("\\") Then
            ' UNC → \\?\UNC\server\share
            Return "\\?\UNC\" & path.Substring(2)
        Else
            ' Normale → \\?\C:\...
            Return "\\?\" & path
        End If
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

        ' Al posto del controllo esistenza 
        Dim createdDirs As New HashSet(Of String)
        Dim createdFiles As New HashSet(Of String)

        ' Crea la directory corrente
        RaiseEvent OnMessage("[DRY]: Creo Destinazione: " & destDir)
        RaiseEvent OnMessage("[DEBUG] ENTER BackupSimulation " & DateTime.Now.ToString("HH:mm:ss.fff"))

        For Each file As String In Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories)

            ' STOP Veloce ogni 1000 file
            If filesCopied Mod 1000 = 0 OrElse (DateTime.Now - lastStopCheck).TotalSeconds >= 2 Then
                lastStopCheck = DateTime.Now
                ' Stop Sicuro
                If System.IO.File.Exists(stopFile) Then
                    ' Se leggo il codice impostato dall'app, devo terminare brutalmente
                    If System.IO.File.ReadAllText(stopFile) = "STOP=" & SecurityStop.ToString Then
                        RaiseEvent OnMessage(String.Format(Environment.NewLine & "STOPPING >>>> {0} / {1}" & vbCrLf, filesCopied, totalFiles))
                        RaiseEvent OnStopped()
                        Exit Sub
                    End If
                End If
            End If

            ' Controllo file esistenza (NO long path qui)
            Dim sourcePath As String = Path.GetFullPath(file)
            Dim normalizedSource As String = NormalizePath(Path.GetFullPath(sourcePath))

            ' Percorso relativo
            Dim relativePath As String = GetRelativePath(sourceDir, file)

            ' Destinazione
            Dim targetFile As String = Path.Combine(destDir, relativePath)
            Dim destPath As String = Path.GetFullPath(targetFile)
            Dim normalizedDest As String = NormalizePath(Path.GetFullPath(destPath))
            Dim tempPath As String = normalizedDest & ".partial"
            Dim normalizedTemp As String = NormalizePath(Path.GetFullPath(tempPath))

            Dim targetDirOnly As String = Path.GetDirectoryName(targetFile)
            If Not createdDirs.Contains(targetDirOnly) Then
                createdDirs.Add(targetDirOnly)
                RaiseEvent OnMessage("[DRY] Creazione Cartella: " & targetDirOnly)
            End If

            ' Size sicura
            Dim size As Long = 0
            size = SafeGetFileSize(sourcePath)
            If size = 0 Then
                If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[DRY] ZERO byte [" & size.ToString() & "] per " & file)
            End If

            ' Debug path lunghi
            If file.Length > 250 Then
                If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[DRY] PATH > 250 → " & file)
            End If

            Dim copied As Boolean = False

            ' Copia Simulata
            If Not createdFiles.Contains(normalizedDest) Then
                createdFiles.Add(normalizedDest)

                If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[DRY] COPIED tmp: " & normalizedTemp)
                If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[DRY] MOVE atomico: " & normalizedDest)

                copied = True
            End If

            Dim attrs = System.IO.File.GetAttributes(normalizedSource)
            ' Date
            If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[DRY] SET Attributi: " & System.IO.File.GetCreationTime(normalizedSource) & " " & destPath)
            If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[DRY] SET Attributi: " & System.IO.File.GetLastWriteTime(normalizedSource) & " " & destPath)
            If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[DRY] SET Attributi: " & System.IO.File.GetLastAccessTime(normalizedSource) & " " & destPath)

            Dim zoneIdentifier As String = destPath & ":Zone.Identifier"
            If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[DRY] ZoneIdentifier: " & zoneIdentifier)

            ' Attrtibuti Speciali
            Dim attrsExtended = System.IO.File.GetAttributes(normalizedSource)
            ' Compressione
            If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[DRY] SET Flag Compresso: " & destPath)
            ' Cifratura
            If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[DRY] SET Flag Encrypted: " & destPath & vbCrLf)

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
        Next

        RaiseEvent OnFinished()
    End Sub


    Public Function SafeCopy(source As String, dest As String, Optional overwrite As Boolean = False, Optional logging As Boolean = False) As Boolean
        ' Semplice copia con Fallback per evitare problemi di file lock o accesso negato, soprattutto su file in uso. Con overwrite = True sovrascrive.
        Dim returned As Boolean = False
        Dim copied As Boolean = False
        Dim fileExist As Boolean = False
        SkippingError = 0

        ' =========================
        ' SIZE SICURO
        ' =========================
        Dim size As Long = 0
        Try
            size = SafeGetFileSize(source)
            If size = 0 Then
                RaiseEvent OnMessage(vbCrLf & "[ZERO] byte (" & size.ToString() & ") per " & source)
                ' Il File esiste
                fileExist = True
            ElseIf size > 0 Then
                ' Il File esiste
                fileExist = True
            End If
        Catch
            If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[ERROR] Size: " & source)
        End Try


        If fileExist Then
            ' Il file esiste e lo sovrascriviamo
            If overwrite Then
                ' =========================
                ' RETRY COPYFILEW - RESILIENT VERSION
                ' =========================
                Dim maxRetries As Integer = 10 ' Almeno 10 tentativi per la rete
                Dim retryDelay As Integer = 2000 ' 2 secondi tra i tentativi (dà tempo alla rete di riprendersi)

                For i As Integer = 1 To maxRetries
                    If BackupEngine.CopyFileWin32(source, dest, False) Then
                        RaiseEvent OnMessage(vbCrLf & $"[COPY] OK: {source}")
                        copied = True
                        Exit For
                    Else

                        Dim err = Marshal.GetLastWin32Error()

                        ' 53: Network path not found
                        ' 64: Network name is no longer available
                        ' 121: Semaphore timeout period has expired (tipico dei grossi file su WiFi instabile)
                        ' 5: Access Denied (magari Samba si è riavviato e sta rinegoziando i permessi)

                        If FullVerbose Then RaiseEvent OnMessage($"[TENTATIVO {i}] Errore API {err} su: {Path.GetFileName(source)}")

                        If i < maxRetries Then
                            ' Se è un errore di rete, forse è meglio aspettare un po' di più
                            If err = 64 Or err = 53 Or err = 121 Then
                                If FullVerbose Then RaiseEvent OnMessage("Rete instabile, attesa riconnessione...")

                                ' Notifica alla Form che deve processare i messaggi
                                RaiseEvent OnYieldRequired()

                                Threading.Thread.Sleep(5000) ' Aspetta 5 secondi se la rete è proprio giù
                            Else
                                Threading.Thread.Sleep(retryDelay) ' Aspetta 2 secondi per errori generici
                            End If
                        Else
                            SkippingError += 1
                            RaiseEvent OnMessage("[ERRORE] Copia in " & maxRetries & " tentativi.")
                            copied = False

                        End If
                    End If
                Next

                ' Siamo usciti dal FOR perchè copiato?
                If Not copied Then
                    ' 🔁 FALLBACK FILESTREAM
                    Try
                        Dim tempDest = dest & ".tmp"

                        Using sourceStream As New FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
                            Using destStream As New FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.None)
                                sourceStream.CopyTo(destStream)
                            End Using
                        End Using

                        ' Sostituzione atomica
                        If System.IO.File.Exists(dest) Then System.IO.File.Delete(dest)
                        System.IO.File.Move(tempDest, dest)
                        RaiseEvent OnMessage(vbCrLf & $"[COPY] OK: {source}")

                        copied = True

                    Catch
                        SkippingError += 1
                        RaiseEvent OnMessage(vbCrLf & $"[ERROR] File non copiato: {source}")
                        returned = False

                        Return returned
                    End Try
                End If

            Else
                ' Non Sovrascrivere la destinazione
                Dim sizeDest As Long = 0
                Try
                    sizeDest = SafeGetFileSize(dest)
                    If sizeDest >= 0 AndAlso System.IO.File.Exists(dest) Then
                        ' Destinazione è zero byte, ma non sovrascrivere comunque
                        copied = False
                        returned = False

                        Return returned
                    Else
                        ' Puoi copiare...

                        ' =========================
                        ' RETRY COPYFILEW - RESILIENT VERSION
                        ' =========================
                        Dim maxRetries As Integer = 10 ' Almeno 10 tentativi per la rete
                        Dim retryDelay As Integer = 2000 ' 2 secondi tra i tentativi (dà tempo alla rete di riprendersi)

                        For i As Integer = 1 To maxRetries
                            If BackupEngine.CopyFileWin32(source, dest, Not overwrite) Then
                                RaiseEvent OnMessage(vbCrLf & $"[COPY] OK: {source}")
                                copied = True
                                Exit For
                            Else
                                Dim err = Marshal.GetLastWin32Error()

                                ' 53: Network path not found
                                ' 64: Network name is no longer available
                                ' 121: Semaphore timeout period has expired (tipico dei grossi file su WiFi instabile)
                                ' 5: Access Denied (magari Samba si è riavviato e sta rinegoziando i permessi)

                                If FullVerbose Then RaiseEvent OnMessage($"[TENTATIVO {i}] Errore API {err} su: {Path.GetFileName(source)}")

                                If i < maxRetries Then
                                    ' Se è un errore di rete, forse è meglio aspettare un po' di più
                                    If err = 64 Or err = 53 Or err = 121 Then
                                        If FullVerbose Then RaiseEvent OnMessage("Rete instabile, attesa riconnessione...")

                                        ' Notifica alla Form che deve processare i messaggi
                                        RaiseEvent OnYieldRequired()

                                        Threading.Thread.Sleep(5000) ' Aspetta 5 secondi se la rete è proprio giù
                                    Else
                                        Threading.Thread.Sleep(retryDelay) ' Aspetta 2 secondi per errori generici
                                    End If
                                Else
                                    SkippingError += 1
                                    RaiseEvent OnMessage("[ERRORE] Copia in " & maxRetries & " tentativi.")
                                    copied = False

                                End If
                            End If
                        Next

                        ' Siamo usciti dal FOR perchè copiato?
                        If Not copied Then
                            ' 🔁 FALLBACK FILESTREAM
                            Try
                                Dim tempDest = dest & ".tmp"

                                Using sourceStream As New FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
                                    Using destStream As New FileStream(tempDest, FileMode.Create, FileAccess.Write, FileShare.None)
                                        sourceStream.CopyTo(destStream)
                                    End Using
                                End Using

                                ' Sostituzione atomica
                                If System.IO.File.Exists(dest) Then System.IO.File.Delete(dest)
                                System.IO.File.Move(tempDest, dest)
                                RaiseEvent OnMessage(vbCrLf & $"[COPY] OK: {source}")

                                copied = True

                            Catch
                                SkippingError += 1
                                RaiseEvent OnMessage(vbCrLf & $"[ERROR] File non copiato: {source}")
                                returned = False

                                Return returned
                            End Try
                        End If

                    End If
                Catch
                    If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[ERROR] Size: " & source)
                End Try

            End If

        Else
            RaiseEvent OnMessage(vbCrLf & $"[ERROR] File non esiste?! {source}")
            copied = False
            returned = False
        End If


        ' 🔁 FALLBACK SICURO su copied per Attributi
        If copied Then

            ' =========================
            ' ATTRIBUTI ORIGINE
            ' =========================
            Dim attrs As FileAttributes = FileAttributes.Normal

            Try
                attrs = System.IO.File.GetAttributes(source)
            Catch
                SkippingError += 1
                RaiseEvent OnMessage(vbCrLf & "[ERROR] Attributi: " & source)
            End Try

            ' Se ReadOnly → rimuovilo temporaneamente
            If (attrs And FileAttributes.ReadOnly) = FileAttributes.ReadOnly Then
                System.IO.File.SetAttributes(dest, attrs And Not FileAttributes.ReadOnly)
            End If

            ' =========================
            ' RIMUOVE ZONE IDENTIFIER
            ' =========================
            Dim zoneIdentifier As String = dest & ":Zone.Identifier"
            Try
                If System.IO.File.Exists(zoneIdentifier) Then
                    System.IO.File.Delete(zoneIdentifier)
                End If
            Catch
                SkippingError += 1
                RaiseEvent OnMessage(vbCrLf & "[ERROR] Impossibile rimuovere Zone.Identifier: " & dest)
            End Try

            ' =========================
            ' DATE
            ' =========================
            Try
                System.IO.File.SetCreationTime(dest, System.IO.File.GetCreationTime(source))
                System.IO.File.SetLastWriteTime(dest, System.IO.File.GetLastWriteTime(source))
                System.IO.File.SetLastAccessTime(dest, System.IO.File.GetLastAccessTime(source))
            Catch
                SkippingError += 1
                RaiseEvent OnMessage(vbCrLf & "[ERROR] Date: " & dest)
            End Try

            ' =========================
            ' RIPRISTINO ATTRIBUTI
            ' =========================
            Try
                System.IO.File.SetAttributes(dest, attrs)
            Catch
                SkippingError += 1
                RaiseEvent OnMessage(vbCrLf & "[ERROR] Set attributi: " & source)
            End Try

            ' =========================
            ' COMPRESSIONE / CIFRATURA
            ' =========================
            Dim attrsExtended As FileAttributes = 0
            Try
                attrsExtended = System.IO.File.GetAttributes(source)
            Catch
                attrsExtended = 0
            End Try
            ' Compressione
            Try
                If IsNtfs(dest) AndAlso (attrsExtended And FileAttributes.Compressed) <> 0 Then
                    BackupEngine.SetCompressed(dest, True)
                End If
            Catch ex As Exception
                SkippingError += 1
                If FullVerbose Then
                    RaiseEvent OnMessage(vbCrLf & "SKIP Flag Compressione: " & source)
                    RaiseEvent OnMessage(vbCrLf & "FS: " & New DriveInfo(Path.GetPathRoot(dest)).DriveFormat & vbCrLf)
                End If
            End Try
            ' Cifratura
            Try
                If IsNtfs(dest) AndAlso (attrsExtended And FileAttributes.Encrypted) <> 0 Then
                    BackupEngine.SetEncrypted(dest, True)
                End If
            Catch ex As Exception
                SkippingError += 1
                If FullVerbose Then
                    RaiseEvent OnMessage(vbCrLf & "SKIP Flag Cifratura: " & source)
                    RaiseEvent OnMessage(vbCrLf & "FS: " & New DriveInfo(Path.GetPathRoot(dest)).DriveFormat & vbCrLf)
                End If
            End Try

            returned = True
        End If

        Return returned
    End Function


    Public Sub SecureDelete(filePath As String, Optional chunkBuffer As Integer = 4096)
        ' Cancella un file in modo sicuro
        If Not System.IO.File.Exists(filePath) Then Return

        Dim length = New System.IO.FileInfo(filePath).Length

        ' Sovrascrittura
        Using fs As New System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Write)

            Dim buffer(chunkBuffer - 1) As Byte
            Dim rng As New System.Security.Cryptography.RNGCryptoServiceProvider()

            Dim written As Long = 0

            While written < length

                rng.GetBytes(buffer)

                Dim toWrite = Math.Min(buffer.Length, length - written)

                fs.Write(buffer, 0, toWrite)

                written += toWrite

            End While

        End Using

        ' Rimozione
        System.IO.File.Delete(filePath)
    End Sub


    Public Sub CopyDirectoryWithDatesSafe(sourceDir As String, destDir As String, totalFiles As Integer, ByRef filesCopied As Integer)
        ' Funzione di copia ricorsiva evoluta
        Dim completedSuccessfully As Boolean = False
        SkippingError = 0
        SkippingFile = 0

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
                    SkippingFile += 1

                    If FullVerbose Then
                        If SkippingFile Mod 100 = 0 Then
                            RaiseEvent OnMessage($"SKIP TEMP: {SkippingFile} file...")
                        End If
                    End If

                    Continue For
                End If


                ' STOP Veloce ogni 1000 file
                If filesCopied Mod 1000 = 0 OrElse (DateTime.Now - lastStopCheck).TotalSeconds >= 2 Then
                    lastStopCheck = DateTime.Now
                    ' Stop Sicuro
                    If System.IO.File.Exists(stopFile) Then
                        ' Se leggo il codice impostato dall'app, devo terminare brutalmente
                        If System.IO.File.ReadAllText(stopFile) = "STOP=" & SecurityStop.ToString Then
                            RaiseEvent OnMessage(String.Format(Environment.NewLine & "STOPPING >>>> {0} / {1}" & vbCrLf, filesCopied, totalFiles))
                            RaiseEvent OnStopped()
                            Exit Sub
                        End If
                    End If
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
                        SkippingError += 1
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
                                SkippingFile += 1

                                If FullVerbose Then
                                    If SkippingFile Mod 100 = 0 Then
                                        RaiseEvent OnMessage($"SKIP: {SkippingFile} file " & file)
                                    End If
                                End If

                                filesCopied += 1
                                If totalFiles > 0 Then
                                    Dim percent As Integer = CInt(filesCopied * 100 / totalFiles)
                                    RaiseEvent OnProgress(percent)
                                End If

                                Continue For
                            End If

                        End If
                    Catch ex As Exception
                        SkippingError += 1
                        If FullVerbose Then RaiseEvent OnMessage("SKIP CHECK ERROR: " & file)
                    End Try

                    ' =========================
                    ' SIZE SICURO
                    ' =========================
                    Dim size As Long = 0
                    Try
                        size = SafeGetFileSize(sourcePath)
                        If size = 0 Then
                            RaiseEvent OnMessage(vbCrLf & "[ZERO] byte (" & size.ToString() & ") per " & file)
                        End If
                    Catch
                        If FullVerbose Then RaiseEvent OnMessage(vbCrLf & "[ERROR] Size: " & file)
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
                    ' RETRY COPYFILEW - RESILIENT VERSION
                    ' =========================
                    Dim maxRetries As Integer = 10 ' Almeno 10 tentativi per la rete
                    Dim retryDelay As Integer = 2000 ' 2 secondi tra i tentativi (dà tempo alla rete di riprendersi)

                    For i As Integer = 1 To maxRetries
                        If BackupEngine.CopyFileWin32(normalizedSource, normalizedTemp, False) Then
                            copied = True
                            Exit For
                        Else
                            Dim err = Marshal.GetLastWin32Error()

                            ' 53: Network path not found
                            ' 64: Network name is no longer available
                            ' 121: Semaphore timeout period has expired (tipico dei grossi file su WiFi instabile)
                            ' 5: Access Denied (magari Samba si è riavviato e sta rinegoziando i permessi)

                            RaiseEvent OnMessage($"[TENTATIVO {i}] Errore API {err} su: {Path.GetFileName(file)}")

                            If i < maxRetries Then
                                ' Se è un errore di rete, forse è meglio aspettare un po' di più
                                If err = 64 Or err = 53 Or err = 121 Then
                                    RaiseEvent OnMessage("Rete instabile, attesa riconnessione...")

                                    ' Notifica alla Form che deve processare i messaggi
                                    RaiseEvent OnYieldRequired()

                                    Threading.Thread.Sleep(5000) ' Aspetta 5 secondi se la rete è proprio giù
                                Else
                                    Threading.Thread.Sleep(retryDelay) ' Aspetta 2 secondi per errori generici
                                End If
                            Else
                                SkippingError += 1
                                RaiseEvent OnMessage("[ERROR] File dopo " & maxRetries & " tentativi.")
                            End If
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
                            RaiseEvent OnMessage(vbCrLf & "[COPY] FALLBACK OK -> " & file)
                        Catch ex As Exception
                            SkippingError += 1
                            RaiseEvent OnMessage(vbCrLf & "[ERROR] FALLBACK: " & ex.Message & " -> " & file)
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

                            If FullVerbose Then RaiseEvent OnMessage("COPIED -> " & normalizedDest)
                        Catch ex As Exception
                            SkippingError += 1
                            RaiseEvent OnMessage("[ERROR] RENAME: " & ex.Message & " -> " & file)
                            copied = False
                        End Try
                    End If

                    ' =========================
                    ' Se ancora non copiato, segnala
                    ' =========================
                    If Not copied Then
                        SkippingError += 1
                        RaiseEvent OnMessage(vbCrLf & "[ERROR] COPIA DEFINITIVO: " & file)
                        Continue For
                    End If

                    ' =========================
                    ' ATTRIBUTI ORIGINE
                    ' =========================
                    Dim attrs As FileAttributes = FileAttributes.Normal

                    Try
                        attrs = System.IO.File.GetAttributes(sourcePath)
                    Catch
                        SkippingError += 1
                        RaiseEvent OnMessage(vbCrLf & "[ERROR] Attributi: " & file)
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
                        SkippingError += 1
                        RaiseEvent OnMessage(vbCrLf & "[ERROR] Impossibile rimuovere Zone.Identifier: " & targetFile)
                    End Try

                    ' =========================
                    ' DATE
                    ' =========================
                    Try
                        System.IO.File.SetCreationTime(destPath, System.IO.File.GetCreationTime(normalizedSource))
                        System.IO.File.SetLastWriteTime(destPath, System.IO.File.GetLastWriteTime(normalizedSource))
                        System.IO.File.SetLastAccessTime(destPath, System.IO.File.GetLastAccessTime(normalizedSource))
                    Catch
                        SkippingError += 1
                        RaiseEvent OnMessage(vbCrLf & "[ERROR] Date: " & targetFile)
                    End Try

                    ' =========================
                    ' RIPRISTINO ATTRIBUTI
                    ' =========================
                    Try
                        System.IO.File.SetAttributes(destPath, attrs)
                    Catch
                        SkippingError += 1
                        RaiseEvent OnMessage(vbCrLf & "[ERROR] Set attributi: " & targetFile)
                    End Try

                    ' =========================
                    ' COMPRESSIONE / CIFRATURA
                    ' =========================
                    Dim attrsExtended As FileAttributes = 0
                    Try
                        attrsExtended = System.IO.File.GetAttributes(normalizedSource)
                    Catch
                        attrsExtended = 0
                    End Try
                    ' Compressione
                    Try
                        If IsNtfs(destPath) AndAlso (attrsExtended And FileAttributes.Compressed) <> 0 Then
                            BackupEngine.SetCompressed(destPath, True)
                        End If
                    Catch ex As Exception
                        SkippingError += 1
                        If FullVerbose Then
                            RaiseEvent OnMessage(vbCrLf & "SKIP Flag Compressione: " & targetFile)
                            RaiseEvent OnMessage(vbCrLf & "FS: " & New DriveInfo(Path.GetPathRoot(destPath)).DriveFormat & vbCrLf)
                        End If
                    End Try
                    ' Cifratura
                    Try
                        If IsNtfs(destPath) AndAlso (attrsExtended And FileAttributes.Encrypted) <> 0 Then
                            BackupEngine.SetEncrypted(destPath, True)
                        End If
                    Catch ex As Exception
                        SkippingError += 1
                        If FullVerbose Then
                            RaiseEvent OnMessage(vbCrLf & "SKIP Flag Cifratura: " & targetFile)
                            RaiseEvent OnMessage(vbCrLf & "FS: " & New DriveInfo(Path.GetPathRoot(destPath)).DriveFormat & vbCrLf)
                        End If
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
                    RaiseEvent OnMessage(vbCrLf & "[ERROR] Accesso negato FILE: " & file)
                Catch ex As Exception
                    RaiseEvent OnMessage(vbCrLf & "[ERROR] FILE: " & file & " - " & ex.Message)
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
                    SkippingError += 1
                    RaiseEvent OnMessage(vbCrLf & "[ERROR] Accesso negato DIR: " & dir)
                Catch ex As Exception
                    SkippingError += 1
                    RaiseEvent OnMessage(vbCrLf & "[ERROR] DIR: " & dir & " - " & ex.Message)
                End Try
            Next

            ' backup loop
            completedSuccessfully = True

        Catch ex As UnauthorizedAccessException
            SkippingError += 1
            RaiseEvent OnMessage(vbCrLf & "[ERROR] Accesso negato DIR principale: " & sourceDir)
        Catch ex As Exception
            SkippingError += 1
            RaiseEvent OnMessage(vbCrLf & "[ERROR] generale: " & ex.Message)
        Finally
            RaiseEvent OnFinished()
        End Try

        If Not completedSuccessfully Then
            RaiseEvent OnMessage($"⚠ ABORTED   - SALTATI: {SkippingFile} ERRORI: {SkippingError}")
        End If
    End Sub


    Private Async Function ExecuteWithRetry(action As Action, Optional maxRetries As Integer = 5) As Task(Of Boolean)
        Dim retryCount As Integer = 0
        Dim delaySeconds As Integer = 5 ' Aspetta 5 secondi tra i tentativi
        SkippingError = 0

        While retryCount < maxRetries
            Try
                action.Invoke()
                Return True ' Successo!
            Catch ex As IOException
                retryCount += 1
                RaiseEvent OnMessage($"[RETE] Connessione persa. Tentativo {retryCount}/{maxRetries} in corso...")

                ' Aspettiamo prima di riprovare
                Task.Delay(delaySeconds * 1000)

                ' Opzionale: raddoppia il tempo di attesa ad ogni errore (Exponential Backoff)
                delaySeconds *= 2
            Catch ex As Exception
                SkippingError += 1
                RaiseEvent OnMessage("[ERRORE FATALE] " & ex.Message)
                Return False
            End Try
        End While

        Return False ' Timeout raggiunto
    End Function


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


    Public Function NormalizePath(input As String) As String
        If String.IsNullOrWhiteSpace(input) Then Return ""

        ' 1. Pulizia spazi e conversione slash stile Linux
        Dim path As String = input.Trim().Replace("/"c, "\"c)
        Try
            path = System.IO.Path.GetFullPath(path)
        Catch
            Return input
        End Try

        ' 2. PROTEZIONE UNC: Se inizia con \\, identifichiamo se è rete o percorso esteso
        If path.StartsWith("\\") Then
            ' Se è già un percorso esteso (\\?\), restituiscilo intatto
            If path.StartsWith("\\?\") Then Return path

            ' Se è un percorso UNC standard (\\server\share), 
            ' assicuriamoci di non "mangiare" i due backslash iniziali
            ' e procediamo con cautela
        End If

        ' 3. Formattazione lettera unità (solo per percorsi locali tipo c:\)
        ' Usiamo un controllo più stringente per non colpire i percorsi di rete
        If path.Length >= 2 AndAlso path(1) = ":"c AndAlso Not path.StartsWith("\\") Then
            path = Char.ToUpper(path(0)) & path.Substring(1)
        End If

        ' 4. Rimozione slash finale (Trailing Slash)
        ' Per i percorsi UNC tipo \\172.29.132.121\backup_test\, 
        ' dobbiamo assicurarci di non ridurlo a \\172.29.132.121 (che non è una cartella valida)
        If path.EndsWith("\") Then
            ' Se è una root locale (C:\) o una share di rete (\\server\share), NON rimuovere lo slash
            If Not IsRootFolder(path) Then
                path = path.TrimEnd("\"c)
            End If
        End If

        Return path
    End Function

    Public Function IsRootFolder(path As String) As Boolean
        ' Funzione di supporto per capire se siamo sulla radice
        ' Caso locale: C:\
        If path.Length <= 3 AndAlso path.EndsWith(":\") Then Return True

        ' Caso UNC: \\server\share\
        Dim parts = Strings.Split(path, "\"c, StringSplitOptions.RemoveEmptyEntries)
        If path.StartsWith("\\") AndAlso parts.Length <= 2 Then Return True

        Return False
    End Function

    Public Sub CreateBackupManifest(sourceDir As String, destDir As String, manifestPath As String)
        Dim manifest As New BackupManifest()
        manifest.BackupDate = DateTime.Now
        manifest.BackupUser = Environment.UserName
        manifest.SourceFolder = sourceDir
        manifest.DestinationFolder = destDir

        Dim totalFiles As Long = 0
        SkippingError = 0
        SkippingFile = 0

        Try
            For Each file As String In Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories)

                ' Skip file temporanei, downloads parziali etc. Il tempo non aspetta :D
                If ShouldSkipFile(file) Then
                    SkippingFile += 1
                    If FullVerbose Then
                        If SkippingFile Mod 100 = 0 Then
                            RaiseEvent OnMessage($"SKIP TEMP: {SkippingFile} file...")
                        End If
                    End If

                    Continue For
                End If

                Try
                    totalFiles += 1
                Catch ex As Exception
                    SkippingError += 1
                    Debug.WriteLine("ERRORE CONTEGGIO FILE: " & file)
                End Try
            Next

        Catch ex As Exception
            SkippingError += 1
            Debug.WriteLine("ERRORE ENUMERAZIONE: " & ex.Message)
        End Try

        manifest.TotalFiles = totalFiles

        Dim totalSize As Long = 0
        Dim runningTotalFiles As Long = 0

        For Each file In Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories)

            ' STOP Veloce ogni 1000 file
            If runningTotalFiles Mod 1000 = 0 OrElse (DateTime.Now - lastStopCheck).TotalSeconds >= 2 Then
                lastStopCheck = DateTime.Now
                ' Stop Sicuro
                If System.IO.File.Exists(stopFile) Then
                    ' Se leggo il codice impostato dall'app, devo terminare brutalmente
                    If System.IO.File.ReadAllText(stopFile) = "STOP=" & SecurityStop.ToString Then
                        RaiseEvent OnMessage(String.Format(Environment.NewLine & "STOPPING >>>> {0} / {1}" & vbCrLf, runningTotalFiles, totalFiles))
                        RaiseEvent OnStopped()
                        Exit Sub
                    End If
                End If
            End If

            ' Skip file temporanei, downloads parziali etc. Il tempo non aspetta :D
            If ShouldSkipFile(file) Then
                SkippingFile += 1

                If FullVerbose Then
                    If SkippingFile Mod 100 = 0 Then
                        RaiseEvent OnMessage($"SKIP TEMP: {SkippingFile} file...")
                    End If
                End If
                Continue For
            End If

            Try
                Dim path = NormalizePath(file)
                If path.Length > 250 Then Debug.WriteLine("PATH LEN > 250: " & path.Length.ToString)

                If Not System.IO.File.Exists(path) Then
                    SkippingError += 1
                    Debug.WriteLine("NOT EXISTS: " & path)
                    Continue For
                End If

                Dim currentFileSize As Long
                Using fs = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    currentFileSize = fs.Length
                End Using

                totalSize += currentFileSize
                runningTotalFiles += 1

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
            .SizeBytes = currentFileSize,
            .CreationTime = creation,
            .LastWriteTime = write,
            .LastAccessTime = access,
            .FullHash = hash,
            .IsCompressed = IsCompressed,
            .IsEncrypted = IsEncrypted
        })

            Catch ex As Exception
                SkippingError += 1
                Debug.WriteLine("ERRORE FILE: " & file)
                Debug.WriteLine(ex.Message)
            End Try

        Next

        manifest.TotalSizeBytes = totalSize

        If totalFiles <> runningTotalFiles Then
            Debug.WriteLine("WARNING: Totale file non congruo. Expected: " & totalFiles & ", Actual: " & runningTotalFiles)
            If runningTotalFiles > totalFiles Then
                manifest.TotalFiles = runningTotalFiles ' correzione
            End If
        End If

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
        SkippingError = 0
        SkippingFile = 0

        ' Leggi JSON del manifest
        Dim json = System.IO.File.ReadAllText(manifestPath)
        Dim manifest = JsonSerializer.Deserialize(Of BackupManifest)(json)
        Dim runningTotalFiles As Long = 0

        For Each file In manifest.FileList

            ' STOP Veloce ogni 1000 file
            If runningTotalFiles Mod 1000 = 0 OrElse (DateTime.Now - lastStopCheck).TotalSeconds >= 2 Then
                lastStopCheck = DateTime.Now
                ' Stop Sicuro
                If System.IO.File.Exists(stopFile) Then
                    ' Se leggo il codice impostato dall'app, devo terminare brutalmente
                    If System.IO.File.ReadAllText(stopFile) = "STOP=" & SecurityStop.ToString Then
                        RaiseEvent OnMessage(String.Format(Environment.NewLine & "STOPPING >>>> {0} / {1}" & vbCrLf, runningTotalFiles, manifest.TotalFiles))
                        RaiseEvent OnStopped()
                        Exit Function
                    End If
                End If
            End If

            Dim destPath = NormalizePath(Path.Combine(manifest.DestinationFolder, file.RelativePath))
            Dim sourcePath = NormalizePath(Path.Combine(manifest.SourceFolder, file.RelativePath))

            'Console.WriteLine("EXPECTED: " & destPath)
            'Console.WriteLine("EXISTS: " & System.IO.File.Exists(destPath))

            ' File Dest mancante
            If Not System.IO.File.Exists(destPath) Then
                SkippingFile += 1

                If FullVerbose Then
                    If SkippingFile Mod 100 = 0 Then
                        RaiseEvent OnMessage($"NOT EXIST DST: " & destPath)
                    End If
                End If

                SkippingError += 1
                result.MissingFiles.Add(file.RelativePath)
                Continue For
            End If

            ' File Sorgente mancante
            If Not System.IO.File.Exists(sourcePath) Then
                SkippingFile += 1

                If FullVerbose Then
                    If SkippingFile Mod 100 = 0 Then
                        RaiseEvent OnMessage($"NOT EXIST SRC: " & sourcePath)
                    End If
                End If

                SkippingError += 1
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
                SkippingError += 1
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

            ' Progresso
            runningTotalFiles += 1
        Next

        RaiseEvent OnMessage(Environment.NewLine & $"SKIP: {SkippingFile}  ERRORI: {SkippingError}")

        Return result
    End Function

    Public Function CountFilesSafe(folder As String) As Integer
        ' conteggio files streaming senza ricorsione per evitare stack overflow su cartelle molto profonde
        Dim count As Integer = 0
        Dim stack As New Stack(Of String)
        SkippingFile = 0
        SkippingError = 0

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
                SkippingError += 1
                RaiseEvent OnMessage("Accesso negato: " & current)
            Catch ex As Exception
                SkippingError += 1
                RaiseEvent OnMessage("Errore su: " & current & " - " & ex.Message)
            End Try

        End While

        RaiseEvent OnMessage(Environment.NewLine & $"SKIP: {SkippingFile}  ERRORI: {SkippingError}")

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

        SkippingError = 0

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

                ' STOP Veloce ogni 1000 file
                If processed Mod 1000 = 0 OrElse (DateTime.Now - lastStopCheck).TotalSeconds >= 2 Then
                    lastStopCheck = DateTime.Now
                    ' Stop Sicuro
                    If System.IO.File.Exists(stopFile) Then
                        ' Se leggo il codice impostato dall'app, devo terminare brutalmente
                        If System.IO.File.ReadAllText(stopFile) = "STOP=" & SecurityStop.ToString Then
                            RaiseEvent OnMessage(String.Format(Environment.NewLine & "STOPPING >>>> {0} / {1}" & vbCrLf, processed, totalFiles))
                            RaiseEvent OnStopped()
                            Exit Function
                        End If
                    End If
                End If


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
                        SkippingError += 1
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
                        SkippingError += 1
                        RaiseEvent OnMessage("ERR fallback: " & ex.Message & " -> " & destFile)
                    End Try
                End If

                If Not copied Then
                    failed += 1
                    SkippingError += 1
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

                If sizeSrc <> -1 OrElse sizeSrc <> -2 Then 'Errori -1 FilenotFound -2 DirectoryNotFound
                    If sizeSrc <> sizeDst Then
                        RaiseEvent OnMessage("SIZE MISMATCH: " & destFile)
                        failed += 1
                        SkippingError += 1
                    Else
                        RaiseEvent OnMessage("OK: " & destFile)
                    End If
                End If


                '=========================
                ' ATTRIBUTI NTFS
                ' =========================
                Try
                    If entry.IsCompressed Then
                        SetCompressed(destFile, True)
                    End If
                Catch ex As Exception
                    SkippingError += 1
                    RaiseEvent OnMessage("COMPRESSION ERROR: " & destFile)
                End Try

                Try
                    If entry.IsEncrypted Then
                        SetEncrypted(destFile, True)
                    End If
                Catch ex As Exception
                    SkippingError += 1
                    RaiseEvent OnMessage("ENCRYPTION ERROR: " & destFile)
                End Try

            Catch ex As Exception
                failed += 1
                SkippingError += 1
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

        RaiseEvent OnMessage(Environment.NewLine & $"[RESTORE] Errori: {SkippingError}")
        RaiseEvent OnMessage(Environment.NewLine & "Restore completato " & Now.ToString)

        Return failed
    End Function


    Public Function RunRestoreMode(manifestPath As String, mode As RestoreMode, Optional overwrite As Boolean = True, Optional restoreRoot As String = Nothing) As Integer
        ' Nuovo con modalità Simulazione (Dry-Run), Overwrite, Merge etc.
        Dim json = System.IO.File.ReadAllText(manifestPath)
        Dim manifest = JsonSerializer.Deserialize(Of BackupManifest)(json)

        SkippingError = 0
        SkippingFile = 0

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
                        SkippingFile += 1
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
                            SkippingFile += 1
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

        RaiseEvent OnMessage(Environment.NewLine & $"[RESTORE] Saltati: {SkippingFile}")
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

        SkippingError = 0

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
            SkippingError += 1
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
            SkippingError += 1
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
            SkippingError += 1
            RaiseEvent OnMessage("COMPRESSION ERROR: " & destFile)
        End Try

        Try
            If entry.IsEncrypted Then
                SetEncrypted(destFile, True)
            End If
        Catch ex As Exception
            SkippingError += 1
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
