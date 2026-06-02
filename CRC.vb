Imports System.IO
Imports System.Security.Cryptography
Imports System.Security.Policy


Public Class CRC

    ' ===================== CORE =====================

    Private Shared Function NormalizePath(path As String) As String

        If String.IsNullOrWhiteSpace(path) Then
            Return path
        End If

        Dim fullPath = System.IO.Path.GetFullPath(path)

        ' UNC path
        If fullPath.StartsWith("\\") Then
            Return "\\?\UNC\" & fullPath.Substring(2)
        End If

        ' Local path
        If Not fullPath.StartsWith("\\?\") Then
            Return "\\?\" & fullPath
        End If

        Return fullPath

    End Function

    Public Shared Function ComputeFullHash(path As String) As String
        Try
            Dim fullPath = NormalizePath(path)

            If Not File.Exists(fullPath) Then
                Debug.WriteLine("FILE NON TROVATO: " & path)
                Return "MISSING"
            End If

            Using sha = Security.Cryptography.SHA256.Create()
                Using stream = New FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Return HashToHex(sha.ComputeHash(stream))
                End Using
            End Using

        Catch ex As Exception
            Debug.WriteLine("HASH ERROR: " & path)
            Debug.WriteLine(ex.Message)
            Return "ERROR"
        End Try
    End Function

    Private Shared Function HashToHex(hash As Byte()) As String
        Return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()
    End Function

    Public Shared Function HexToBytes(hex As String) As Byte()
        Dim bytes(hex.Length \ 2 - 1) As Byte
        For i = 0 To bytes.Length - 1
            bytes(i) = Convert.ToByte(hex.Substring(i * 2, 2), 16)
        Next
        Return bytes
    End Function

    ' ===================== FAST HASH =====================

    Public Shared Function ComputeFastHash(fi As FileInfo) As Integer
        Try
            Dim fullPath = NormalizePath(fi.FullName)

            If Not File.Exists(fullPath) Then Return 0

            Dim hash As UInteger = &H811C9DC5UI

            Dim size = fi.Length
            hash = (hash Xor CUInt(size And &HFFFFFFFFUI)) * &H1000193UI
            hash = (hash Xor CUInt((size >> 32) And &HFFFFFFFFUI)) * &H1000193UI

            Dim ticks = fi.LastWriteTimeUtc.Ticks
            hash = (hash Xor CUInt(ticks And &HFFFFFFFFUI)) * &H1000193UI
            hash = (hash Xor CUInt((ticks >> 32) And &HFFFFFFFFUI)) * &H1000193UI

            Using fs = New FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

                Dim buffer(4095) As Byte

                Dim read = fs.Read(buffer, 0, buffer.Length)
                For i = 0 To read - 1
                    hash = (hash Xor buffer(i)) * &H1000193UI
                Next

                If fs.Length > buffer.Length Then
                    fs.Seek(-buffer.Length, SeekOrigin.End)
                    read = fs.Read(buffer, 0, buffer.Length)

                    For i = 0 To read - 1
                        hash = (hash Xor buffer(i)) * &H1000193UI
                    Next
                End If

            End Using

            Return CInt(hash And &H7FFFFFFF)

        Catch ex As Exception
            Debug.WriteLine("FAST HASH ERROR: " & fi.FullName)
            Return 0
        End Try
    End Function

    Public Shared Function ComputeSuperFastHash(path As String) As Integer
        ' Modalità ultra veloce
        Try
            Dim fullPath = NormalizePath(path)
            Dim hash As UInteger = &H811C9DC5UI

            Using fs = New FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

                Dim buffer(4095) As Byte

                Dim read = fs.Read(buffer, 0, buffer.Length)
                For i = 0 To read - 1
                    hash = (hash Xor buffer(i)) * &H1000193UI
                Next

                If fs.Length > buffer.Length Then
                    fs.Seek(-buffer.Length, SeekOrigin.End)
                    read = fs.Read(buffer, 0, buffer.Length)

                    For i = 0 To read - 1
                        hash = (hash Xor buffer(i)) * &H1000193UI
                    Next
                End If

            End Using

            Return CInt(hash And &H7FFFFFFF)

        Catch ex As Exception
            Debug.WriteLine("SUPERFAST HASH ERROR: " & path)
            Return 0
        End Try
    End Function

    ' ===================== KEYS =====================

    Public Shared Function BuildKey(size As Long, lastWrite As DateTime) As String
        Return $"{size}_{lastWrite.ToUniversalTime().Ticks}"
    End Function

    Public Shared Function GetFileKey(fi As FileInfo) As String
        Return $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}"
    End Function

    Public Shared Function GetExtendedKey(fi As FileInfo) As String
        Dim fast = ComputeFastHash(fi)
        Return $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}_{fast}"
    End Function

    ' ===================== SHA =====================

    Public Shared Function CreateSHA256sum(filename As String) As String
        Return ComputeFullHash(filename)
    End Function

    Private Shared Function CreateCRCAA(filename As String) As String
        Try
            Dim fullPath = NormalizePath(filename)

            Using sha256 = Security.Cryptography.SHA256.Create()
                Using sha512 = Security.Cryptography.SHA512.Create()
                    Using stream = New FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

                        Dim hash256 = HashToHex(sha256.ComputeHash(stream))

                        stream.Position = 0
                        Dim hash512 = HashToHex(sha512.ComputeHash(stream))

                        Return hash256
                    End Using
                End Using
            End Using

        Catch ex As Exception
            Debug.WriteLine("CRCAA ERROR: " & filename)
            Return "ERROR"
        End Try
    End Function

    ' ===================== DTC =====================

    '---------------------   CRCAA Terapeutico Zero Collisioni   ---------------------------
    'CRCAA  = db7abba433f9f7aa789a21a7072a7d9e6bb2859d5de61db88008075f4b98959dbbafcacdbabfff
    'CRCAAn = db7abba433f9f7aa789a21a7072a7d9e6bb2859d5de61db88008075f4b98959d11052023101555
    '---------------------   SHA ????  Collisioni   ----------------------------------------
    'sha256 = db7abba433f9f7aa789a21a7072a7d9e6bb2859d5de61db88008075f4b98959d
    '----- Devi trovare 2 file con lo stesso orario al SEC e lo stesso CONTENUTO -----------
    'CRC256 = db7abba433f9f7aa789a21a7072a7d9e6bb2859d5de61db88008075f4b98959d
    'DataOra= 11/05/2023 10:15:55                                             bbafcacdbabfff
    '---------------------------------  Se li trovi, sono uno la copia dell'altro. [RISOLTO]


    Public Shared Function DTCMaker(filename As String) As String
        ' Crea una stringa che rappresenta il CRC con la data di ultima modifica con l'orario
        '   aggiornati al secondo, quindi zero collisioni con l'orario
        ' Aggangiata alla SHA Sum del file, es:
        ' CRCAA  = db7abba433f9f7aa789a21a7072a7d9e6bb2859d5de61db88008075f4b98959dbbafcacdbabfff ' Zero collisioni
        ' CRCAAN = db7abba433f9f7aa789a21a7072a7d9e6bb2859d5de61db88008075f4b98959d11052023101555 ' Zero coll num.
        ' SHA256 = db7abba433f9f7aa789a21a7072a7d9e6bb2859d5de61db88008075f4b98959d               ' Consigliato
        Try
            Dim fi As New FileInfo(filename)
            If Not fi.Exists Then Return "MISSING"

            Dim dMod As Date = fi.LastWriteTime
            Dim dtc As String = dMod.ToString("ddMMyyyyHHmmss")

            Dim wordsCRC As String = "abcdefghijklmnopqrstuvxywz"
            Dim dtcw As String = ""

            For j = 0 To dtc.Length - 1
                Dim n As Integer = Integer.Parse(dtc(j))
                dtcw &= wordsCRC.Substring(n, 1)
            Next

            Return CreateCRCAA(filename) & dtcw

        Catch ex As Exception
            Debug.WriteLine("DTC ERROR: " & filename)
            Return "ERROR"
        End Try
    End Function

    Public Shared Function DTCReader(CRCAA As String) As (String, String)
        Dim Counter = 0
        Dim wordsCRC As String = "abcdefghijklmnopqrstuvxywz"
        Dim Start = CRCAA.Length - 14
        Dim Fine = CRCAA.Length - 1
        Dim dataCRC As String = ""

        For j = Start To Fine
            Dim c As String = CRCAA(j)
            dataCRC &= Strings.InStr(wordsCRC, c) - 1

            If Counter = 1 OrElse Counter = 3 Then dataCRC &= "/"
            If Counter = 7 Then dataCRC &= " "
            If Counter = 9 OrElse Counter = 11 Then dataCRC &= ":"

            Counter += 1
        Next

        Dim sha256 As String = CRCAA.Substring(0, 64)
        Return (sha256, dataCRC)
    End Function

End Class