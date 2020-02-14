Public Class Form1
    Private TargetDevice As Dataq.Devices.DI1110.Device
    Private taskRead As Task
    Private cancelRead As CancellationTokenSource
    ' Define class that will print the data to the GUI
    Private progress As IProgress(Of String) =
        New Progress(Of String)(Sub(s As String)
                                    tbOutput.AppendText(s)
                                End Sub)

    Private Async Sub btnState_Click(sender As Object, e As EventArgs) Handles btnState.Click
        btnState.Enabled = False
        If cancelRead IsNot Nothing Then
            'get here if an acquisition process is in progress and we've been commanded to stop

            cancelRead.Cancel() 'cancel the read process
            cancelRead = Nothing
            Await taskRead 'wait for the read process to complete
            taskRead = Nothing
            Await TargetDevice.AcquisitionStopAsync() 'stop the device from acquiring 
            ' Change button text/state
            btnState.Text = "Start"
            GroupBox1.Enabled = True
            GroupBox2.Enabled = True
            tbSampleRate.Enabled = True
            tbRateFilter.Enabled = True
            btnDigOut.Enabled = False

        Else
            'get here if we're starting a new acquisition process
            TargetDevice.Channels.Clear() 'initialize the device
            ConfigureAnalogChannels()
            ConfigureDigitalChannels()
            If SampleRateBad() Then
                'get here if requested sample rate is out of range
                'It's a bust, so...
                btnState.Enabled = True
                Exit Sub
            End If
            'otherwise, the selected sample rate is good, so use it
            TargetDevice.SetSampleRateOnChannels(tbSampleRate.Text)
            Try
                Await TargetDevice.InitializeAsync() 'configure the device as defined. Errors if no channels are enabled
            Catch ex As Exception
                'Detect if no channels are enabled, and bail if so. 
                MessageBox.Show("Please enable at least one analog channel or digital port.",
                                "No Enabled Channels", MessageBoxButtons.OK, MessageBoxIcon.Error)
                btnState.Enabled = True
                Exit Sub
            End Try

            'now determine what sample rate per channel the device is using from the 
            'first enabled input channel, and display it
            Dim FirstInChannel As Dataq.Devices.DI1110.ChannelIn
            Dim NoInputChannels As Boolean = True
            For index = 0 To TargetDevice.Channels.Count - 1
                If TypeOf TargetDevice.Channels(index) Is Dataq.Devices.IChannelIn Then
                    FirstInChannel = TargetDevice.Channels(index)
                    NoInputChannels = False
                    Exit For
                End If
            Next
            If NoInputChannels Then
                MessageBox.Show("Please configure at least one analog channel or digital port as an input",
                                "No Inputs Enabled", MessageBoxButtons.OK, MessageBoxIcon.Error)
                btnState.Enabled = True
                Exit Sub
            End If
            'Everything is good, so...
            btnState.Text = "Stop" 'change button text to "Stop" from "Start"
            cancelRead = New CancellationTokenSource() ' Create the cancellation token
            Await TargetDevice.AcquisitionStartAsync() 'start acquiring

            ' NOTE: assumes at least one input channel enabled
            ' Start a task in the background to read data
            taskRead = Task.Run(Async Function()
                                    'capture the first channel programmed as an input (MasterChannel)
                                    'and use it to track data availability for all input channels
                                    Dim MasterChannel As Dataq.Devices.IChannelIn = Nothing
                                    For index = 0 To TargetDevice.Channels.Count
                                        If TypeOf TargetDevice.Channels(index) Is Dataq.Devices.IChannelIn Then
                                            MasterChannel = TargetDevice.Channels(index) ' we have our channel 
                                            Exit For
                                        End If
                                    Next

                                    ' Keep reading while acquiring data
                                    While TargetDevice.IsAcquiring
                                        ' Read data and catch if cancelled (to exit loop and continue)
                                        Try
                                            'throws an error if acquisition has been cancelled
                                            'otherwise refreshes the buffer DataIn with new data
                                            'ReadDataAsync moves data from a small, temp buffer between USB hadrware and Windows
                                            'into the SDK's DataIn buffer. ReadDataAsync should be called frequently to prevent a buffer
                                            'overflow at the hardware level. However, buffer DataIn can grow to the size of available RAM if necessary.
                                            Await TargetDevice.ReadDataAsync(cancelRead.Token)
                                        Catch ex As OperationCanceledException
                                            'get here if acquisition cancelled
                                            Exit While
                                        End Try
                                        'get here if acquisition is still active
                                        If MasterChannel.DataIn.Count = 0 Then
                                            'get here if no data in the channel buffer
                                            Continue While
                                        End If

                                        'The option to suppress time-consuming display output allows faster sampling rates
                                        'than the program could otherwise support
                                        If cbSuppressDisplay.Checked = True Then
                                            For Each ch In TargetDevice.Channels
                                                If TypeOf ch Is Dataq.Devices.IChannelIn Then
                                                    CType(ch, Dataq.Devices.IChannelIn).DataIn.Clear()
                                                End If
                                            Next
                                            Continue While
                                        End If

                                        ' We have data. Convert it to strings
                                        Dim temp As String = ""
                                        Dim temp1 As String = ""
                                        ' NOTE: assuming all input channels contain exact same amount of data
                                        'Think of data for each input channel organized in columns, 
                                        'one column per channel (DataIn (index)). Rows represent scans.
                                        'Data is extracted by the following routine 
                                        For index = 0 To MasterChannel.DataIn.Count - 1 ' this is the row (scan) counter
                                            For Each ch In TargetDevice.Channels 'this is the column (channel) counter
                                                'get a channel value and convert it to a string
                                                'format the output string depending upon the type of input
                                                If (TypeOf ch Is Dataq.Devices.IAnalogVoltage) Or
                                                (TypeOf ch Is Dataq.Devices.IFrequency) Then
                                                    temp1 = CType(ch, Dataq.Devices.IChannelIn).DataIn(index).ToString("0.0000")
                                                ElseIf (TypeOf ch Is Dataq.Devices.IChannelIn) Then
                                                    'must be either dig in or count so we need just a single integer
                                                    temp1 = CType(ch, Dataq.Devices.IChannelIn).DataIn(index).ToString("0")
                                                Else
                                                    'must be dig switch, so suppress output
                                                    Continue For
                                                End If

                                                temp1 = temp1 + ", "
                                                temp = temp + temp1 'append the channel value to the output

                                            Next ' get the next channel
                                            'get here when a channel row is complete
                                            'strip the trailing comma from the last column position
                                            temp = temp.Substring(0, temp.Length - 2) + Environment.NewLine
                                        Next 'get the next row
                                        ' purge displayed data from the buffer
                                        For Each ch In TargetDevice.Channels
                                            If TypeOf ch Is Dataq.Devices.IChannelIn Then
                                                CType(ch, Dataq.Devices.IChannelIn).DataIn.Clear()
                                            End If
                                        Next
                                        ' Report the data to be printed to the GUI
                                        progress.Report(temp)
                                    End While
                                    progress.Report("Stopped" + Environment.NewLine)
                                End Function, cancelRead.Token)
            GroupBox1.Enabled = False
            GroupBox2.Enabled = False
            tbSampleRate.Enabled = False
            tbRateFilter.Enabled = False
            btnDigOut.Enabled = True
        End If
        ' Enable button once operation has completed
        btnState.Enabled = True
    End Sub

    Function SampleRateBad() As Boolean
        'ensure that the sample rate per channel is not out of range
        Dim SampleRateRange As Dataq.Collections.IReadOnlyRange(Of Double) =
            CType(TargetDevice.Channels(0), Dataq.Devices.Dynamic.IReadOnlyChannelSampleRate).GetSupportedSampleRateRange(TargetDevice)

        lblActual.Text = Math.Round(SampleRateRange.Minimum, 2).ToString + " - " + Math.Round(SampleRateRange.Maximum, 2).ToString
        If (Not SampleRateRange.ContainsValue(tbSampleRate.Text)) Then
            MessageBox.Show("Selected sample rate is outside the range of " + SampleRateRange.Minimum.ToString + " to " + SampleRateRange.Maximum.ToString + " Hz inclusive for your current channel settings.",
                            "Sample Rate Error", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return True
        End If
        Return False
    End Function

    Sub ConfigureDigitalChannels()
        Dim DigitalInChan As Dataq.Devices.DI1110.DigitalIn
        Dim DigitalOutChan As Dataq.Devices.DI1110.DigitalOut
        Dim RateInChan As Dataq.Devices.DI1110.FrequencyIn
        Dim CountInChan As Dataq.Devices.DI1110.CounterIn

        'configure digtial port 0
        If cbD0.Checked Then
            Select Case True
                Case cmbD0Mode.Text = "Switch"
                    DigitalOutChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalOut), 0),
                        Dataq.Devices.DI1110.DigitalOut)
                Case Else 'else has to be an input
                    DigitalInChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalIn), 0),
                        Dataq.Devices.DI1110.DigitalIn)
            End Select
        End If

        'configure digtial port 1
        If cbD1.Checked Then
            Select Case True
                Case cmbD1Mode.Text = "Switch"
                    DigitalOutChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalOut), 1),
                        Dataq.Devices.DI1110.DigitalOut)
                Case Else 'else has to be an input
                    DigitalInChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalIn), 1),
                        Dataq.Devices.DI1110.DigitalIn)
            End Select
        End If

        'configure digtial port 2
        If cbD2.Checked Then
            Select Case True
                Case cmbD2Mode.Text = "Switch"
                    DigitalOutChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalOut), 2),
                        Dataq.Devices.DI1110.DigitalOut)
                Case cmbD2Mode.Text.Contains("Rate")
                    'rate channels are identified in logical order, so this is rate channel 1
                    RateInChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.FrequencyIn), 1),
                        Dataq.Devices.DI1110.FrequencyIn)
                    RateInChan.AveragingFactor = CType(tbRateFilter.Text, Integer)
                    Select Case True
                        Case cmbD2Mode.Text.Contains("50 kHz")
                            RateInChan.FrequencyRange.Maximum = 50000
                        Case cmbD2Mode.Text.Contains("20 kHz")
                            RateInChan.FrequencyRange.Maximum = 20000
                        Case cmbD2Mode.Text.Contains("10 kHz")
                            RateInChan.FrequencyRange.Maximum = 10000
                        Case cmbD2Mode.Text.Contains("5 kHz")
                            RateInChan.FrequencyRange.Maximum = 5000
                        Case cmbD2Mode.Text.Contains("2 kHz")
                            RateInChan.FrequencyRange.Maximum = 2000
                        Case cmbD2Mode.Text.Contains("1 kHz")
                            RateInChan.FrequencyRange.Maximum = 1000
                        Case cmbD2Mode.Text.Contains("500 Hz")
                            RateInChan.FrequencyRange.Maximum = 500
                        Case cmbD2Mode.Text.Contains("200 Hz")
                            RateInChan.FrequencyRange.Maximum = 200
                        Case cmbD2Mode.Text.Contains("100 Hz")
                            RateInChan.FrequencyRange.Maximum = 100
                        Case cmbD2Mode.Text.Contains("50 Hz")
                            RateInChan.FrequencyRange.Maximum = 50
                        Case cmbD2Mode.Text.Contains("20 Hz")
                            RateInChan.FrequencyRange.Maximum = 20
                        Case cmbD2Mode.Text.Contains("10 Hz")
                            RateInChan.FrequencyRange.Maximum = 10
                    End Select
                Case Else 'else has to be a discrete input
                    DigitalInChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalIn), 2),
                        Dataq.Devices.DI1110.DigitalIn)
            End Select
        End If

        'configure digtial port 3
        If cbD3.Checked Then
            Select Case True
                Case cmbD3Mode.Text = "Switch"
                    DigitalOutChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalOut), 3),
                        Dataq.Devices.DI1110.DigitalOut)
                Case cmbD3Mode.Text = "Count"
                    'count channels are identified in logical order, so this is count channel 1
                    CountInChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.CounterIn), 1),
                        Dataq.Devices.DI1110.CounterIn)
                Case Else 'else has to be a discrete input
                    DigitalInChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalIn), 3),
                        Dataq.Devices.DI1110.DigitalIn)
            End Select
        End If

        'configure digtial port 4
        If cbD4.Checked Then
            Select Case True
                Case cmbD4Mode.Text = "Switch"
                    DigitalOutChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalOut), 4),
                        Dataq.Devices.DI1110.DigitalOut)
                Case Else 'else has to be an input
                    DigitalInChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalIn), 4),
                        Dataq.Devices.DI1110.DigitalIn)
            End Select
        End If

        'configure digtial port 5
        If cbD5.Checked Then
            Select Case True
                Case cmbD5Mode.Text = "Switch"
                    DigitalOutChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalOut), 5),
                        Dataq.Devices.DI1110.DigitalOut)
                Case Else 'else has to be an input
                    DigitalInChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalIn), 5),
                        Dataq.Devices.DI1110.DigitalIn)
            End Select
        End If

        'configure digtial port 6
        If cbD6.Checked Then
            Select Case True
                Case cmbD6Mode.Text = "Switch"
                    DigitalOutChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalOut), 6),
                        Dataq.Devices.DI1110.DigitalOut)
                Case Else 'else has to be an input
                    DigitalInChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.DigitalIn), 6),
                        Dataq.Devices.DI1110.DigitalIn)
            End Select
        End If
    End Sub

    Sub ConfigureAnalogChannels()
        Dim AnalogChan As Dataq.Devices.DI1110.AnalogVoltageIn

        If cbCH1.Checked Then
            'configure analog channel 1
            AnalogChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.AnalogVoltageIn), 1),
                Dataq.Devices.DI1110.AnalogVoltageIn)
        End If

        If cbCH2.Checked Then
            'configure analog channel 2
            AnalogChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.AnalogVoltageIn), 2),
                Dataq.Devices.DI1110.AnalogVoltageIn)
        End If

        If cbCH3.Checked Then
            'configure analog channel 3
            AnalogChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.AnalogVoltageIn), 3),
                Dataq.Devices.DI1110.AnalogVoltageIn)
        End If

        If cbCH4.Checked Then
            'configure analog channel 4
            AnalogChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.AnalogVoltageIn), 4),
                Dataq.Devices.DI1110.AnalogVoltageIn)
        End If

        If cbCH5.Checked Then
            'configure analog channel 5
            AnalogChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.AnalogVoltageIn), 5),
                Dataq.Devices.DI1110.AnalogVoltageIn)
        End If

        If cbCH6.Checked Then
            'configure analog channel 6
            AnalogChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.AnalogVoltageIn), 6),
                Dataq.Devices.DI1110.AnalogVoltageIn)
        End If

        If cbCH7.Checked Then
            'configure analog channel 7
            AnalogChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.AnalogVoltageIn), 7),
                Dataq.Devices.DI1110.AnalogVoltageIn)
        End If

        If cbCH8.Checked Then
            'configure analog channel 8
            AnalogChan = CType(TargetDevice.ChannelFactory(GetType(Dataq.Devices.DI1110.AnalogVoltageIn), 8),
                Dataq.Devices.DI1110.AnalogVoltageIn)
        End If
    End Sub

    Private Sub Form1_FormClosing(sender As Object, e As EventArgs) Handles MyBase.FormClosing
        If TargetDevice IsNot Nothing Then
            TargetDevice.Dispose()
            TargetDevice = Nothing
        End If
    End Sub

    Private Async Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load

        cmbD0Mode.SelectedIndex = 0
        cmbD1Mode.SelectedIndex = 0
        cmbD2Mode.SelectedIndex = 0
        cmbD3Mode.SelectedIndex = 0
        cmbD4Mode.SelectedIndex = 0
        cmbD5Mode.SelectedIndex = 0
        cmbD6Mode.SelectedIndex = 0
        cmbDigOut0.SelectedIndex = 0
        cmbDigOut1.SelectedIndex = 0
        cmbDigOut2.SelectedIndex = 0
        cmbDigOut3.SelectedIndex = 0
        cmbDigOut4.SelectedIndex = 0
        cmbDigOut5.SelectedIndex = 0
        cmbDigOut6.SelectedIndex = 0
        ' Get a list of devices with model DI-1110
        Dim AllDevices As IReadOnlyList(Of Dataq.Devices.IDevice) =
            Await Dataq.Misc.Discovery.ByModelAsync(GetType(Dataq.Devices.DI1110.Device))
        If AllDevices.Count > 0 Then
            tbOutput.AppendText("Found a DI-1110." + Environment.NewLine)
            ' Cast first device from generic device to specific DI-1110 type
            TargetDevice = CType(AllDevices(0), Dataq.Devices.DI1110.Device)
            Await TargetDevice.ConnectAsync()
            ' Ensure it's stopped
            Await TargetDevice.AcquisitionStopAsync()
            ' Query device for some info
            Await TargetDevice.QueryDeviceAsync()
            ' Print queried info to GUI
            tbOutput.AppendText("Manufacturer: " + TargetDevice.Manufacturer + Environment.NewLine)
            tbOutput.AppendText("Model: " + TargetDevice.Model + Environment.NewLine)
            tbOutput.AppendText("Serial number: " + TargetDevice.Serial + Environment.NewLine)
            tbOutput.AppendText("Firmware revision: " + TargetDevice.FirmwareRevision.ToString() + Environment.NewLine)
            ' Enable the next usable button
            btnState.Enabled = True
        Else
            tbOutput.AppendText("No DI-1110 found." + Environment.NewLine)
        End If
    End Sub

    Private Async Sub btnDigOut_Click(sender As Object, e As EventArgs) Handles btnDigOut.Click
        'define a flag to indicate if at least one output port is defined
        Dim OutputExists As Boolean = False
        ' Store desired output states in array for easy access
        Dim DigOut(7) As Double
        DigOut(0) = Double.Parse(cmbDigOut0.Text)
        DigOut(1) = Double.Parse(cmbDigOut1.Text)
        DigOut(2) = Double.Parse(cmbDigOut2.Text)
        DigOut(3) = Double.Parse(cmbDigOut3.Text)
        DigOut(4) = Double.Parse(cmbDigOut4.Text)
        DigOut(5) = Double.Parse(cmbDigOut5.Text)
        DigOut(6) = Double.Parse(cmbDigOut6.Text)
        ' Loop through output channels
        For Each chan In TargetDevice.Channels
            If TypeOf chan Is Dataq.Devices.DI1110.DigitalOut Then
                Dim temp As Dataq.Devices.DI1110.DigitalOut =
                    CType(chan, Dataq.Devices.DI1110.DigitalOut)
                ' Clear any previous data
                temp.DataOut.Clear()
                ' Add desired output value to channel
                temp.DataOut.Add(DigOut(temp.Number))
                OutputExists = True
            End If
        Next
        ' Ooutput data only if there's at least one enabled for output
        If OutputExists Then
            Await TargetDevice.SingleScanOutAsync()
        End If
    End Sub

    Private Sub cbD0_CheckedChanged(sender As Object, e As EventArgs) Handles cbD0.CheckedChanged
        cmbD0Mode.Enabled = cbD0.Checked
        cmbDigOut0.Enabled = cbD0.Checked And cmbD0Mode.Text = "Switch"
    End Sub

    Private Sub cbD1_CheckedChanged(sender As Object, e As EventArgs) Handles cbD1.CheckedChanged
        cmbD1Mode.Enabled = cbD1.Checked
        cmbDigOut1.Enabled = cbD1.Checked And cmbD1Mode.Text = "Switch"
    End Sub

    Private Sub cbD2_CheckedChanged(sender As Object, e As EventArgs) Handles cbD2.CheckedChanged
        cmbD2Mode.Enabled = cbD2.Checked
        cmbDigOut2.Enabled = cbD2.Checked And cmbD2Mode.Text = "Switch"
    End Sub

    Private Sub cbD3_CheckedChanged(sender As Object, e As EventArgs) Handles cbD3.CheckedChanged
        cmbD3Mode.Enabled = cbD3.Checked
        cmbDigOut3.Enabled = cbD3.Checked And cmbD3Mode.Text = "Switch"
    End Sub

    Private Sub cbD4_CheckedChanged(sender As Object, e As EventArgs) Handles cbD4.CheckedChanged
        cmbD4Mode.Enabled = cbD4.Checked
        cmbDigOut4.Enabled = cbD4.Checked And cmbD4Mode.Text = "Switch"
    End Sub

    Private Sub cbD5_CheckedChanged(sender As Object, e As EventArgs) Handles cbD5.CheckedChanged
        cmbD5Mode.Enabled = cbD5.Checked
        cmbDigOut5.Enabled = cbD5.Checked And cmbD5Mode.Text = "Switch"
    End Sub

    Private Sub cbD6_CheckedChanged(sender As Object, e As EventArgs) Handles cbD6.CheckedChanged
        cmbD6Mode.Enabled = cbD6.Checked
        cmbDigOut6.Enabled = cbD6.Checked And cmbD6Mode.Text = "Switch"
    End Sub

    Private Sub cmbD0Mode_SelectedIndexChanged(sender As Object, e As EventArgs) Handles cmbD0Mode.SelectedIndexChanged
        cmbDigOut0.Enabled = cmbD0Mode.Text = "Switch"
    End Sub

    Private Sub cmbD1Mode_SelectedIndexChanged(sender As Object, e As EventArgs) Handles cmbD1Mode.SelectedIndexChanged
        cmbDigOut1.Enabled = cmbD1Mode.Text = "Switch"
    End Sub

    Private Sub cmbD2Mode_SelectedIndexChanged(sender As Object, e As EventArgs) Handles cmbD2Mode.SelectedIndexChanged
        cmbDigOut2.Enabled = cmbD2Mode.Text = "Switch"
    End Sub

    Private Sub cmbD3Mode_SelectedIndexChanged(sender As Object, e As EventArgs) Handles cmbD3Mode.SelectedIndexChanged
        cmbDigOut3.Enabled = cmbD3Mode.Text = "Switch"
    End Sub

    Private Sub cmbD4Mode_SelectedIndexChanged(sender As Object, e As EventArgs) Handles cmbD4Mode.SelectedIndexChanged
        cmbDigOut4.Enabled = cmbD4Mode.Text = "Switch"
    End Sub

    Private Sub cmbD5Mode_SelectedIndexChanged(sender As Object, e As EventArgs) Handles cmbD5Mode.SelectedIndexChanged
        cmbDigOut5.Enabled = cmbD5Mode.Text = "Switch"
    End Sub

    Private Sub cmbD6Mode_SelectedIndexChanged(sender As Object, e As EventArgs) Handles cmbD6Mode.SelectedIndexChanged
        cmbDigOut6.Enabled = cmbD6Mode.Text = "Switch"
    End Sub

    Private Async Sub ComboBox1_SelectedIndexChanged(sender As Object, e As EventArgs) Handles cmbLED.SelectedIndexChanged
        Select Case True
            Case cmbLED.Text = "Black"
                Await TargetDevice.Protocol.SetLedColorAsync(0)
            Case cmbLED.Text = "Blue"
                Await TargetDevice.Protocol.SetLedColorAsync(1)
            Case cmbLED.Text = "Green"
                Await TargetDevice.Protocol.SetLedColorAsync(2)
            Case cmbLED.Text = "Cyan"
                Await TargetDevice.Protocol.SetLedColorAsync(3)
            Case cmbLED.Text = "Red"
                Await TargetDevice.Protocol.SetLedColorAsync(4)
            Case cmbLED.Text = "Magenta"
                Await TargetDevice.Protocol.SetLedColorAsync(5)
            Case cmbLED.Text = "Yellow"
                Await TargetDevice.Protocol.SetLedColorAsync(6)
            Case cmbLED.Text = "White"
                Await TargetDevice.Protocol.SetLedColorAsync(7)
        End Select
    End Sub
End Class
