Imports System.Xml
Imports System.Text
Imports System.Runtime.Remoting.Messaging
Imports System.Reflection
Imports System.Globalization

Partial Public Class Plugin

    ' UPnP device-description identity. Declared once here — the description is written from two
    ' places (UpnpDevice, UpnpServer) and they must not disagree.
    Friend Const DeviceManufacturer As String = "yaiol"
    Friend Const DeviceModelDescription As String = "MusicBee UPnP Plugin (Server, Player, Renderer)"

    Friend MustInherit Class UpnpDevice
        Public ReadOnly Udn As Guid
        Public ReadOnly DeviceType As String = "urn:schemas-upnp-org:device:MediaServer:1"
        Public ReadOnly Services As New List(Of UpnpService)
        Public State As DeviceState = DeviceState.NotStarted
        Protected ReadOnly server As UpnpServer

        Public Sub New(udn As Guid)
            Me.Udn = udn
            server = New UpnpServer(Me)
        End Sub

        Public Overridable Sub Start()
            If State <> DeviceState.NotStarted Then
                Exit Sub
            End If
            State = DeviceState.Starting
            Try
                server.Start()
            Catch ex As Exception
                State = DeviceState.NotStarted
                LogError(ex, "UPnPDevice:Start")
                Throw
            End Try
            State = DeviceState.Started
        End Sub

        Public Overridable Sub [Stop]()
            If State <> DeviceState.Started Then
                Exit Sub
            End If
            State = DeviceState.Stopping
            server.Stop()
            State = DeviceState.NotStarted
        End Sub

        Public Sub Restart(includeHttpServer As Boolean)
            State = DeviceState.Starting
            Try
                server.Restart(includeHttpServer)
            Catch
                State = DeviceState.NotStarted
                Throw
            End Try
            State = DeviceState.Started
        End Sub

        Public Sub WriteDescription(writer As XmlTextWriter, wmcCompat As Boolean)
            writer.WriteElementString("deviceType", DeviceType)
            If Not wmcCompat Then
                writer.WriteElementString("friendlyName", Settings.ServerName)
                writer.WriteElementString("manufacturer", DeviceManufacturer)
                writer.WriteElementString("manufacturerURL", UpdateCheck.SITE)
                writer.WriteElementString("modelDescription", DeviceModelDescription)
                writer.WriteElementString("modelName", UpdateCheck.ProductName())
                writer.WriteElementString("modelURL", "http://getmusicbee.com/")
                writer.WriteElementString("modelNumber", UpdateCheck.DisplayVersion())
            Else
                writer.WriteElementString("friendlyName", Settings.ServerName & ":1")
                writer.WriteElementString("manufacturer", DeviceManufacturer)
                ' ⚠ modelName + modelNumber below stay "Windows Media Player Sharing" / "12" ON PURPOSE:
                ' this branch exists to look like WMP to a Windows Media Connect client. They are the
                ' impersonation itself, not stale metadata - do not "correct" them.
                writer.WriteElementString("modelDescription", DeviceModelDescription)
                writer.WriteElementString("modelName", "Windows Media Player Sharing")
                writer.WriteElementString("modelNumber", "12")
            End If
            writer.WriteElementString("serialNumber", "")
            writer.WriteElementString("UDN", "uuid:" & Udn.ToString())
            WriteSpecificDescription(writer)
            writer.WriteStartElement("serviceList")
            For Each service As UpnpService In Services
                writer.WriteStartElement("service")
                service.WriteDescription(writer)
                writer.WriteEndElement()
            Next service
            writer.WriteEndElement()
        End Sub

        Protected MustOverride Sub WriteSpecificDescription(writer As XmlTextWriter)
    End Class  ' UpnpDevice

    Friend Enum DeviceState
        NotStarted = 0
        Starting
        Started
        Stopping
    End Enum  ' DeviceState
End Class  ' UpnpDevice
