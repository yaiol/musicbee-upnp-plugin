Imports System.Text
Imports System.Xml
Imports System.IO
Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets

Partial Public Class Plugin
    Friend Class UpnpServer
        Public ReadOnly RootDevice As UpnpDevice
        Public ReadOnly HttpServer As HttpServer
        Public ReadOnly SsdpServer As SsdpServer
        Private wmcDescriptionData() As Byte
        Private defaultDescriptionData() As Byte
        Private rendererDescriptionData() As Byte

        ' F2.01 - embedded MediaRenderer device (Phase 1). Hosted on THIS server (no second port).
        ' RendererEnabled is snapshotted at construction; toggling the setting needs a MusicBee restart.
        Public ReadOnly RendererEnabled As Boolean
        Public ReadOnly RendererUdn As Guid
        Public ReadOnly RendererServices As New List(Of UpnpService)

        Public Sub New(rootDevice As UpnpDevice)
            Me.RootDevice = rootDevice
            SsdpServer = New SsdpServer(Me)
            HttpServer = New HttpServer
            HttpServer.AddRoute("GET", "/description.xml", New HttpRouteDelegate(AddressOf GetDescription))
            RendererEnabled = Settings.EnableMediaRenderer
            If RendererEnabled Then
                ' Stable, distinct UDN derived from the root device's UDN (no extra persistence needed).
                Dim bytes() As Byte = rootDevice.Udn.ToByteArray()
                bytes(15) = CByte(bytes(15) Xor &H5A)
                RendererUdn = New Guid(bytes)
                ' Services self-register their control/SCPD/event routes on HttpServer in their constructors.
                RendererServices.Add(New RendererConnectionManagerService(Me))
                RendererServices.Add(New RendererAvTransportService(Me))
                RendererServices.Add(New RendererRenderingControlService(Me))
                ' Option B - the renderer is its OWN root device with its own description doc.
                HttpServer.AddRoute("GET", "/renderer.xml", New HttpRouteDelegate(AddressOf GetRendererDescription))
            End If
        End Sub

        Public Sub Start()
            wmcDescriptionData = GetDeviceDescription(True)
            defaultDescriptionData = GetDeviceDescription(False)
            If RendererEnabled Then rendererDescriptionData = BuildRendererDescription()
            HttpServer.Start()
            SsdpServer.Start()
        End Sub

        Public Sub [Stop]()
            SsdpServer.Stop()
            HttpServer.Stop()
        End Sub

        Public Sub Restart(includeHttpServer As Boolean)
            wmcDescriptionData = GetDeviceDescription(True)
            defaultDescriptionData = GetDeviceDescription(False)
            If RendererEnabled Then rendererDescriptionData = BuildRendererDescription()
            If includeHttpServer Then
                HttpServer.Stop()
                HttpServer.Start()
            End If
            SsdpServer.Restart()
        End Sub

        Private Function GetDeviceDescription(wmcCompat As Boolean) As Byte()
            Using stream As New IO.MemoryStream, _
                  writer As New XmlTextWriter(stream, New UTF8Encoding(False))
                writer.WriteRaw("<?xml version=""1.0"" encoding=""UTF-8""?>")
                writer.WriteStartElement("root")
                writer.WriteAttributeString("xmlns", "urn:schemas-upnp-org:device-1-0")
                writer.WriteAttributeString("xmlns:dlna", "urn:schemas-dlna-org:device-1-0")
                writer.WriteStartElement("specVersion")
                writer.WriteElementString("major", "1")
                writer.WriteElementString("minor", "0")
                writer.WriteEndElement()
                writer.WriteStartElement("device")
                RootDevice.WriteDescription(writer, wmcCompat)
                writer.WriteEndElement()
                writer.WriteEndElement()
                writer.Flush()
                Return stream.ToArray()
            End Using
        End Function

        Private Sub GetDescription(request As HttpRequest)
            Dim profile As StreamingProfile = Settings.GetStreamingProfile(request.Headers)
            Dim descriptionData() As Byte = If(profile.WmcCompatability, wmcDescriptionData, defaultDescriptionData)
            Dim response As HttpResponse = request.Response
            response.AddHeader(HttpHeader.ContentLength, descriptionData.Length.ToString())
            response.AddHeader(HttpHeader.ContentType, "text/xml; charset=""utf-8""")
            Using stream As New IO.MemoryStream(descriptionData)
                response.SendHeaders()
                stream.CopyTo(response.Stream)
            End Using
        End Sub

        ' F2.01 Option B - standalone root-device description for the MediaRenderer, served at
        ' /renderer.xml. Separate document + separate SSDP root-device announcements mean control
        ' points see ONE clean "MusicBee (yaiol)" renderer, not a dupe of the server device.
        Private Function BuildRendererDescription() As Byte()
            Using stream As New IO.MemoryStream, _
                  writer As New XmlTextWriter(stream, New UTF8Encoding(False))
                writer.WriteRaw("<?xml version=""1.0"" encoding=""UTF-8""?>")
                writer.WriteStartElement("root")
                writer.WriteAttributeString("xmlns", "urn:schemas-upnp-org:device-1-0")
                writer.WriteAttributeString("xmlns:dlna", "urn:schemas-dlna-org:device-1-0")
                writer.WriteStartElement("specVersion")
                writer.WriteElementString("major", "1")
                writer.WriteElementString("minor", "0")
                writer.WriteEndElement()
                writer.WriteStartElement("device")
                writer.WriteElementString("deviceType", "urn:schemas-upnp-org:device:MediaRenderer:1")
                writer.WriteElementString("friendlyName", If(String.IsNullOrEmpty(Settings.RendererName), "MusicBee (yaiol)", Settings.RendererName))
                writer.WriteElementString("manufacturer", DeviceManufacturer)
                writer.WriteElementString("manufacturerURL", UpdateCheck.SITE)
                writer.WriteElementString("modelDescription", DeviceModelDescription)
                writer.WriteElementString("modelName", UpdateCheck.ProductName())
                writer.WriteElementString("modelURL", "http://getmusicbee.com/")
                writer.WriteElementString("modelNumber", UpdateCheck.DisplayVersion())
                writer.WriteElementString("serialNumber", "")
                writer.WriteElementString("UDN", "uuid:" & RendererUdn.ToString())
                writer.WriteElementString("dlna", "X_DLNADOC", "urn:schemas-dlna-org:device-1-0", "DMR-1.50")
                writer.WriteStartElement("iconList")
                For Each size As String In New String() {"64", "48"}
                    writer.WriteStartElement("icon")
                    writer.WriteElementString("mimetype", "image/jpeg")
                    writer.WriteElementString("width", size)
                    writer.WriteElementString("height", size)
                    writer.WriteElementString("depth", "32")
                    writer.WriteElementString("url", String.Format("/web/images/htmllogo{0}.png", size))
                    writer.WriteEndElement()
                Next size
                writer.WriteEndElement()
                writer.WriteStartElement("serviceList")
                For Each service As UpnpService In RendererServices
                    writer.WriteStartElement("service")
                    service.WriteDescription(writer)
                    writer.WriteEndElement()
                Next service
                writer.WriteEndElement()
                writer.WriteEndElement()
                writer.WriteEndElement()
                writer.Flush()
                Return stream.ToArray()
            End Using
        End Function

        Private Sub GetRendererDescription(request As HttpRequest)
            Dim response As HttpResponse = request.Response
            response.AddHeader(HttpHeader.ContentLength, rendererDescriptionData.Length.ToString())
            response.AddHeader(HttpHeader.ContentType, "text/xml; charset=""utf-8""")
            Using stream As New IO.MemoryStream(rendererDescriptionData)
                response.SendHeaders()
                stream.CopyTo(response.Stream)
            End Using
        End Sub
    End Class  ' UpnpServer
End Class
