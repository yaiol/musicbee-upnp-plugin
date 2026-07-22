Imports System.Windows.Forms

Partial Public Class Plugin
    ' Combo-shaped lookalike for the Group By picker on the Paths tab.
    '
    ' Looks exactly like a stock ComboBox (sunken border, textbox area, chevron) but
    ' clicking - or pressing F4 / Alt-Down - opens a ContextMenuStrip built from
    ' Plugin.View.PickerCommonFields + per-theme submenus (People, Mood & Context,
    ' Album & Work, Ratings & Status, Technical) + dynamic Custom / Virtual submenus.
    '
    ' Native ComboBox can't do submenus - its dropdown is a flat list. We keep the
    ' ComboBox visual rhythm (so the Paths tab still has three stacked combos that
    ' look like combos) but route the open gesture into a ContextMenuStrip instead.
    '
    ' Data model: the ComboBox holds exactly ONE Item - a FieldOption for the
    ' currently-selected field. SelectedIndex stays at 0; SelectedItem is read by
    ' the existing SettingsDialog code via the same FieldOption type it always used.
    ' So from the outside this control is interchangeable with the old ComboBox.
    '
    ' Suppress events: setting the displayed field programmatically (e.g. when the
    ' user switches templates and we restore the path's stored field) must NOT fire
    ' SelectedIndexChanged. We use the existing viewsSuppressEvents pattern via a
    ' BeginSilentUpdate / EndSilentUpdate scope on the SettingsDialog side, and
    ' don't re-raise the event during SetSelectedField below.
    Friend NotInheritable Class FieldPickerCombo
        Inherits ComboBox

        Private Const WM_LBUTTONDOWN As Integer = &H201
        Private Const WM_LBUTTONDBLCLK As Integer = &H203
        Private Const WM_KEYDOWN As Integer = &H100

        ' Single ContextMenuStrip per picker instance, rebuilt lazily on first open
        ' AND on every open thereafter - the Custom/Virtual submenus depend on which
        ' slots MB currently reports as user-named, and that can change while the
        ' dialog is open if the user is also editing tags.
        Private pickerMenu As ContextMenuStrip

        ' Currently-stored canonical field name (e.g. "AlbumArtist", "Custom3", "").
        Private currentField As String = ""

        ' Data-access category of the template being edited. Standard (default) shows the full
        ' field menu; Radio/Podcast show only that category's allowed fields (a flat list), so
        ' the user can't pick a field the node's data source can't deliver. Set by SettingsDialog
        ' whenever the edited template changes.
        Public Property FieldCategory As Plugin.PathCategory = Plugin.PathCategory.Standard

        ' Raised after the user picks an item from the menu. The handler reads
        ' SelectedField for the new value. Mirrors ComboBox's SelectedIndexChanged
        ' so existing wiring (viewsField_SelectedIndexChanged) can subscribe.
        Public Event FieldPicked(sender As Object, e As EventArgs)

        Public Sub New()
            Me.DropDownStyle = ComboBoxStyle.DropDownList
            Me.FormattingEnabled = True
        End Sub

        ' The canonical field name the picker is currently holding. Empty string = none.
        Public ReadOnly Property SelectedField As String
            Get
                Return currentField
            End Get
        End Property

        ' Set the displayed field. Used by SettingsDialog when restoring a template's
        ' stored hierarchy into the editor. Does NOT raise FieldPicked.
        Public Sub SetSelectedField(fieldName As String)
            currentField = If(fieldName, "")
            Dim displayLabel As String
            If String.IsNullOrEmpty(currentField) Then
                displayLabel = Plugin.L("ViewsFieldNone")
            Else
                displayLabel = Plugin.View.FieldDisplayName(currentField)
            End If
            Me.Items.Clear()
            Me.Items.Add(displayLabel)
            Me.SelectedIndex = 0
        End Sub

        Protected Overrides Sub WndProc(ByRef m As Message)
            ' Intercept the mouse / keyboard gestures that would normally open the
            ' native dropdown, and show our ContextMenuStrip instead. We don't pass
            ' the message to the base implementation so the native dropdown never
            ' appears (which would briefly flash an empty list under our menu).
            If m.Msg = WM_LBUTTONDOWN OrElse m.Msg = WM_LBUTTONDBLCLK Then
                Me.Focus()
                ShowPicker()
                Return
            ElseIf m.Msg = WM_KEYDOWN Then
                Dim key As Keys = CType(m.WParam.ToInt32() And &HFFFF, Keys)
                If key = Keys.F4 OrElse (key = Keys.Down AndAlso (Control.ModifierKeys And Keys.Alt) <> 0) Then
                    ShowPicker()
                    Return
                End If
            End If
            MyBase.WndProc(m)
        End Sub

        ' Build a fresh menu (so Custom/Virtual reflect current MB state) and show it
        ' aligned to the bottom of this control.
        Private Sub ShowPicker()
            If Not Me.Enabled Then Return
            pickerMenu = BuildMenu()
            pickerMenu.Show(Me, New Drawing.Point(0, Me.Height))
        End Sub

        ' WinForms treats "&" in a menu item's Text as an Alt-mnemonic prefix: the "&" is
        ' consumed and the following character underlined. Double it so labels like
        ' "Mood & Context" (or a custom tag named with "&") render the ampersand literally.
        Private Shared Function Esc(label As String) As String
            Return If(label, "").Replace("&", "&&")
        End Function

        Private Sub BuildSubmenu(parent As ToolStripDropDownItem, fields() As String)
            For Each fieldName As String In fields
                Dim label As String = Plugin.View.FieldDisplayName(fieldName)
                Dim item As New ToolStripMenuItem(Esc(label)) With {.Tag = fieldName}
                If String.Equals(fieldName, currentField, StringComparison.OrdinalIgnoreCase) Then
                    item.Checked = True
                End If
                AddHandler item.Click, AddressOf MenuItem_Click
                parent.DropDownItems.Add(item)
            Next
        End Sub

        Private Function BuildMenu() As ContextMenuStrip
            Dim m As New ContextMenuStrip()
            ' (none) at the very top - same semantic as the old combo's first entry.
            Dim noneItem As New ToolStripMenuItem(Esc(Plugin.L("ViewsFieldNone"))) With {.Tag = ""}
            If String.IsNullOrEmpty(currentField) Then noneItem.Checked = True
            AddHandler noneItem.Click, AddressOf MenuItem_Click
            m.Items.Add(noneItem)
            m.Items.Add(New ToolStripSeparator())
            ' Restricted categories (Radio / Podcast): a flat list of only the fields their
            ' data source delivers - no themed submenus, no Custom/Virtual. Keeps the picker
            ' honest (you can't pick a field the node can't honour).
            If FieldCategory <> Plugin.PathCategory.Standard Then
                Dim allowed() As String = If(FieldCategory = Plugin.PathCategory.Radio,
                                             Plugin.View.RadioCategoryFields,
                                             Plugin.View.PodcastCategoryFields)
                For Each fieldName As String In allowed
                    Dim label As String = Plugin.View.FieldDisplayName(fieldName)
                    Dim item As New ToolStripMenuItem(Esc(label)) With {.Tag = fieldName}
                    If String.Equals(fieldName, currentField, StringComparison.OrdinalIgnoreCase) Then
                        item.Checked = True
                    End If
                    AddHandler item.Click, AddressOf MenuItem_Click
                    m.Items.Add(item)
                Next
                Return m
            End If
            ' Top-level Common fields (flat, no submenu).
            For Each fieldName As String In Plugin.View.PickerCommonFields
                Dim label As String = Plugin.View.FieldDisplayName(fieldName)
                Dim item As New ToolStripMenuItem(Esc(label)) With {.Tag = fieldName}
                If String.Equals(fieldName, currentField, StringComparison.OrdinalIgnoreCase) Then
                    item.Checked = True
                End If
                AddHandler item.Click, AddressOf MenuItem_Click
                m.Items.Add(item)
            Next
            m.Items.Add(New ToolStripSeparator())
            ' Themed submenus.
            Dim people As New ToolStripMenuItem(Esc(Plugin.L("ViewsFieldGroupPeople")))
            BuildSubmenu(people, Plugin.View.PickerPeopleFields)
            m.Items.Add(people)
            Dim mood As New ToolStripMenuItem(Esc(Plugin.L("ViewsFieldGroupMood")))
            BuildSubmenu(mood, Plugin.View.PickerMoodFields)
            m.Items.Add(mood)
            Dim albumWork As New ToolStripMenuItem(Esc(Plugin.L("ViewsFieldGroupAlbumWork")))
            BuildSubmenu(albumWork, Plugin.View.PickerAlbumWorkFields)
            m.Items.Add(albumWork)
            Dim ratings As New ToolStripMenuItem(Esc(Plugin.L("ViewsFieldGroupRatings")))
            BuildSubmenu(ratings, Plugin.View.PickerRatingFields)
            m.Items.Add(ratings)
            Dim technical As New ToolStripMenuItem(Esc(Plugin.L("ViewsFieldGroupTechnical")))
            BuildSubmenu(technical, Plugin.View.PickerTechnicalFields)
            m.Items.Add(technical)
            ' Custom + Virtual submenus - only added when at least one slot is named.
            Dim customSlots As New List(Of String)
            For i As Integer = 1 To 16
                Dim fieldName As String = "Custom" & i
                Dim mdt As Plugin.MetaDataType = Plugin.View.FieldNameToMetaDataType(fieldName)
                If mdt = 0 Then Continue For
                Dim label As String = Nothing
                Try
                    label = Plugin.mbApiInterface.Setting_GetFieldName(mdt)
                Catch
                End Try
                If String.IsNullOrEmpty(label) Then Continue For
                If String.Equals(label, "Custom " & i, StringComparison.OrdinalIgnoreCase) Then Continue For
                If String.Equals(label, fieldName, StringComparison.OrdinalIgnoreCase) Then Continue For
                customSlots.Add(fieldName)
            Next
            If customSlots.Count > 0 Then
                Dim custom As New ToolStripMenuItem(Esc(Plugin.L("ViewsFieldGroupCustom")))
                BuildSubmenu(custom, customSlots.ToArray())
                m.Items.Add(custom)
            End If
            Dim virtualSlots As New List(Of String)
            For i As Integer = 1 To 25
                Dim fieldName As String = "Virtual" & i
                Dim mdt As Plugin.MetaDataType = Plugin.View.FieldNameToMetaDataType(fieldName)
                If mdt = 0 Then Continue For
                Dim label As String = Nothing
                Try
                    label = Plugin.mbApiInterface.Setting_GetFieldName(mdt)
                Catch
                End Try
                If String.IsNullOrEmpty(label) Then Continue For
                If String.Equals(label, "Virtual " & i, StringComparison.OrdinalIgnoreCase) Then Continue For
                If String.Equals(label, fieldName, StringComparison.OrdinalIgnoreCase) Then Continue For
                virtualSlots.Add(fieldName)
            Next
            If virtualSlots.Count > 0 Then
                Dim virt As New ToolStripMenuItem(Esc(Plugin.L("ViewsFieldGroupVirtual")))
                BuildSubmenu(virt, virtualSlots.ToArray())
                m.Items.Add(virt)
            End If
            Return m
        End Function

        Private Sub MenuItem_Click(sender As Object, e As EventArgs)
            Dim item As ToolStripMenuItem = TryCast(sender, ToolStripMenuItem)
            If item Is Nothing Then Return
            Dim fieldName As String = TryCast(item.Tag, String)
            If fieldName Is Nothing Then fieldName = ""
            currentField = fieldName
            Dim displayLabel As String
            If String.IsNullOrEmpty(currentField) Then
                displayLabel = Plugin.L("ViewsFieldNone")
            Else
                displayLabel = Plugin.View.FieldDisplayName(currentField)
            End If
            Me.Items.Clear()
            Me.Items.Add(displayLabel)
            Me.SelectedIndex = 0
            RaiseEvent FieldPicked(Me, EventArgs.Empty)
        End Sub
    End Class
End Class
