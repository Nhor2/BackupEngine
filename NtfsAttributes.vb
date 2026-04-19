Imports System.Runtime.InteropServices
Imports Microsoft.Win32.SafeHandles

Public Module NtfsAttributes

    ' Compressione NTFS
    Public Const COMPRESSION_FORMAT_NONE As UShort = 0
    Public Const COMPRESSION_FORMAT_DEFAULT As UShort = 1
    Public Const FSCTL_SET_COMPRESSION As UInteger = &H9C040
    Public GENERIC_READ_WRITE As UInteger = &HC0000000UI

    <DllImport("kernel32.dll", SetLastError:=True)>
    Public Function CreateFile(
        lpFileName As String,
        dwDesiredAccess As UInt32,
        dwShareMode As UInt32,
        lpSecurityAttributes As IntPtr,
        dwCreationDisposition As UInt32,
        dwFlagsAndAttributes As UInt32,
        hTemplateFile As IntPtr
    ) As SafeFileHandle
    End Function

    <DllImport("kernel32.dll", SetLastError:=True)>
    Public Function DeviceIoControl(
        hDevice As SafeFileHandle,
        dwIoControlCode As UInteger,
        ByRef lpInBuffer As UShort,
        nInBufferSize As UInteger,
        lpOutBuffer As IntPtr,
        nOutBufferSize As UInteger,
        ByRef lpBytesReturned As UInteger,
        lpOverlapped As IntPtr
    ) As Boolean
    End Function

    ' EFS
    <DllImport("advapi32.dll", SetLastError:=True, CharSet:=CharSet.Unicode)>
    Public Function EncryptFile(lpFileName As String) As Boolean
    End Function

    <DllImport("advapi32.dll", SetLastError:=True, CharSet:=CharSet.Unicode)>
    Public Function DecryptFile(lpFileName As String, dwFlags As UInt32) As Boolean
    End Function

End Module