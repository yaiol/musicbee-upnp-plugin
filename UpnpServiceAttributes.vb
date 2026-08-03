Imports System.Text

Partial Public Class Plugin
    <AttributeUsage(AttributeTargets.[Class], AllowMultiple:=True)> _
    Friend NotInheritable Class UpnpServiceVariable
        Inherits Attribute
        Private m_name As String
        Private m_dataType As String
        Private m_sendEvents As Boolean
        Private m_allowedValue As String()

        Public Sub New(name As String, dataType As String, sendEvents As Boolean, ParamArray allowedValue As String())
            m_name = name
            m_dataType = dataType
            m_sendEvents = sendEvents
            m_allowedValue = allowedValue
        End Sub

        Public Sub New(name As String, dataType As String, sendEvents As Boolean)
            Me.New(name, dataType, sendEvents, New String(-1) {})
        End Sub

        Public ReadOnly Property Name() As String
            Get
                Return m_name
            End Get
        End Property

        Public ReadOnly Property DataType() As String
            Get
                Return m_dataType
            End Get
        End Property

        Public ReadOnly Property SendEvents() As Boolean
            Get
                Return m_sendEvents
            End Get
        End Property

        Public ReadOnly Property AllowedValue() As String()
            Get
                Return m_allowedValue
            End Get
        End Property

        ' <allowedValueRange> — the NUMERIC counterpart of allowedValueList, and mandatory for any
        ' variable whose scale a control point has to know (RenderingControl's Volume above all).
        ' Set as named attribute arguments: <UpnpServiceVariable("Volume", "ui2", False,
        ' Minimum:="0", Maximum:="100", [Step]:="1")>. Emitted only when Minimum and Maximum are
        ' both present, so every other variable is unaffected.
        '
        ' ⚠ Leaving these off a Volume variable is NOT cosmetic: with no declared maximum the
        ' controller has to invent one, and its guess silently rescales every volume in both
        ' directions. Symfonium assumed 69, so its 100% set MusicBee to 69% and MusicBee's 100%
        ' displayed as 144% on the phone (reported 2026-08-03).
        Public Property Minimum() As String
        Public Property Maximum() As String
        Public Property [Step]() As String
    End Class  ' UpnpServiceVariable

    <AttributeUsage(AttributeTargets.Parameter Or AttributeTargets.Method, AllowMultiple:=True)> _
    Friend NotInheritable Class UpnpServiceArgument
        Inherits Attribute
        Private m_index As Integer
        Private m_name As String
        Private m_relatedStateVariable As String

        Public Sub New(index As Integer, name As String, relatedStateVariable As String)
            m_index = index
            m_name = name
            m_relatedStateVariable = relatedStateVariable
        End Sub

        Public Sub New(relatedStateVariable As String)
            m_relatedStateVariable = relatedStateVariable
        End Sub

        Public ReadOnly Property Index() As Integer
            Get
                Return m_index
            End Get
        End Property

        Public ReadOnly Property Name() As String
            Get
                Return m_name
            End Get
        End Property

        Public ReadOnly Property RelatedStateVariable() As String
            Get
                Return m_relatedStateVariable
            End Get
        End Property
    End Class  ' UpnpServiceArgument
End Class