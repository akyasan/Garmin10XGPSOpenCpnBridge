using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Garmin GPS 10x NMEA TCP Bridge")]
[assembly: System.Reflection.AssemblyProduct("Garmin GPS 10x NMEA TCP Bridge")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace GarminGpsBridge
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = BridgeSelfTest.Run();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private static readonly byte[] Gps10xNmeaModeCommand =
        {
            0x10, 0x0a, 0x02, 0x26, 0x00, 0xce, 0x10, 0x03
        };

        private readonly ComboBox _inputPort = new ComboBox();
        private readonly ComboBox _baudRate = new ComboBox();
        private readonly TextBox _tcpPort = new TextBox();
        private readonly Label _status = new Label();
        private readonly Label _nmeaStatus = new Label();
        private readonly Label _fixStatus = new Label();
        private readonly TextBox _log = new TextBox();
        private readonly Button _refresh = new Button();
        private readonly Button _connect = new Button();
        private readonly Button _disconnect = new Button();
        private readonly Button _switchToNmea = new Button();
        private readonly Button _start = new Button();
        private readonly Button _stop = new Button();
        private readonly System.Windows.Forms.Timer _statsTimer = new System.Windows.Forms.Timer();
        private readonly NmeaLineMonitor _nmeaMonitor = new NmeaLineMonitor();

        private SerialPort _gpsPort;
        private NmeaTcpServer _server;
        private CancellationTokenSource _streamCancellation;
        private Task _gpsReadTask;
        private bool _connectingGps;
        private long _gpsToNetworkBytes;

        public MainForm()
        {
            Text = "Garmin GPS 10x NMEA TCP Bridge";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 500);
            Size = new Size(900, 620);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            BuildInterface();
            RefreshPorts();

            _statsTimer.Interval = 1000;
            _statsTimer.Tick += delegate
            {
                if (_server != null)
                {
                    _status.Text = String.Format(
                        "NMEA TCP server 127.0.0.1:{0}  |  clients: {1}  |  sent: {2:N0} bytes",
                        _server.Port,
                        _server.ClientCount,
                        Interlocked.Read(ref _gpsToNetworkBytes));
                }
            };
        }

        private void BuildInterface()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(16);
            root.ColumnCount = 2;
            root.RowCount = 7;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            Label title = new Label();
            title.Text = "Garmin GPS 10x NMEA TCP Bridge";
            title.Font = new Font(Font.FontFamily, 16F, FontStyle.Bold);
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.MiddleLeft;
            root.Controls.Add(title, 0, 0);
            root.SetColumnSpan(title, 2);

            ConfigureCombo(_inputPort);
            root.Controls.Add(MakeFieldLabel("Bluetooth GPS input"), 0, 1);
            root.Controls.Add(_inputPort, 1, 1);

            ConfigureCombo(_baudRate);
            _baudRate.Items.AddRange(new object[] { "4800", "9600", "19200", "38400", "57600", "115200" });
            _baudRate.SelectedItem = "4800";
            root.Controls.Add(MakeFieldLabel("GPS serial baud rate"), 0, 2);
            root.Controls.Add(_baudRate, 1, 2);

            _tcpPort.Dock = DockStyle.Left;
            _tcpPort.Width = 100;
            _tcpPort.Text = "10110";
            root.Controls.Add(MakeFieldLabel("OpenCPN TCP port"), 0, 3);
            root.Controls.Add(_tcpPort, 1, 3);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.FlowDirection = FlowDirection.LeftToRight;
            buttons.WrapContents = true;
            _refresh.Text = "Refresh ports";
            _connect.Text = "Connect GPS";
            _disconnect.Text = "Disconnect GPS";
            _switchToNmea.Text = "Request NMEA mode";
            _start.Text = "Start TCP stream";
            _stop.Text = "Stop stream";
            _disconnect.Enabled = false;
            _stop.Enabled = false;
            foreach (Button button in new[] { _refresh, _connect, _disconnect, _switchToNmea, _start, _stop })
            {
                button.AutoSize = true;
                buttons.Controls.Add(button);
            }
            root.Controls.Add(new Label(), 0, 4);
            root.Controls.Add(buttons, 1, 4);

            _fixStatus.Dock = DockStyle.Fill;
            _fixStatus.Text = "GPS fix: not reported yet.";
            _fixStatus.ForeColor = Color.DarkGoldenrod;
            _fixStatus.TextAlign = ContentAlignment.MiddleLeft;
            root.Controls.Add(_fixStatus, 0, 5);
            root.SetColumnSpan(_fixStatus, 2);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Fill;
            root.Controls.Add(bottom, 0, 6);
            root.SetColumnSpan(bottom, 2);

            _status.Dock = DockStyle.Top;
            _status.Height = 25;
            _status.Text = "Ready.";
            _status.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            bottom.Controls.Add(_status);

            _nmeaStatus.Dock = DockStyle.Top;
            _nmeaStatus.Height = 24;
            _nmeaStatus.Text = "NMEA monitor: waiting for GPS data.";
            bottom.Controls.Add(_nmeaStatus);

            _log.Dock = DockStyle.Fill;
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.WordWrap = false;
            _log.Font = new Font("Consolas", 9F);
            _log.MaxLength = 60000;
            bottom.Controls.Add(_log);
            _log.BringToFront();

            _refresh.Click += delegate { RefreshPorts(); };
            _connect.Click += ConnectGps;
            _disconnect.Click += delegate { DisconnectGps(); };
            _switchToNmea.Click += SwitchToNmea;
            _start.Click += StartTcpStream;
            _stop.Click += delegate { StopTcpStream(); };
            _inputPort.SelectedIndexChanged += delegate { UpdateButtonState(); };
            _baudRate.SelectedIndexChanged += delegate { UpdateButtonState(); };
            _tcpPort.TextChanged += delegate { UpdateButtonState(); };
            FormClosing += delegate
            {
                StopTcpStream();
                DisconnectGps();
            };

            Log("This build does not use com0com. It sends NMEA 0183 over a loopback TCP server.");
            Log("OpenCPN: add a TCP network connection to 127.0.0.1:" + _tcpPort.Text + ", direction Input.");
            Log("The server listens only on this PC; it is not exposed to the local network.");
            Log("4800 baud is the default NMEA serial rate; change it if the receiver is configured differently.");
        }

        private static void ConfigureCombo(ComboBox combo)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Dock = DockStyle.Fill;
        }

        private static Label MakeFieldLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        private void RefreshPorts()
        {
            if (_server != null || _connectingGps || _gpsPort != null)
                return;

            string oldInput = _inputPort.SelectedItem as string;
            try
            {
                List<string> inputs = SerialPort.GetPortNames()
                    .Where(PortPairCatalog.IsComPort)
                    .OrderBy(PortPairCatalog.PortNumber)
                    .ToList();

                _inputPort.Items.Clear();
                foreach (string input in inputs)
                    _inputPort.Items.Add(input);

                if (oldInput != null && _inputPort.Items.Contains(oldInput))
                    _inputPort.SelectedItem = oldInput;
                else if (_inputPort.Items.Contains("COM4"))
                    _inputPort.SelectedItem = "COM4";
                else if (_inputPort.Items.Count > 0)
                    _inputPort.SelectedIndex = 0;

                SetStatus(inputs.Count == 0 ? "No serial COM ports found." : "Ready. Connect to the Bluetooth GPS.");
                UpdateButtonState();
            }
            catch (UnauthorizedAccessException ex)
            {
                ShowPortDiscoveryError(ex);
            }
            catch (IOException ex)
            {
                ShowPortDiscoveryError(ex);
            }
            catch (InvalidOperationException ex)
            {
                ShowPortDiscoveryError(ex);
            }
        }

        private void ShowPortDiscoveryError(Exception ex)
        {
            SetStatus("Could not enumerate serial ports: " + ex.Message);
            Log("Port discovery error: " + ex);
        }

        private void UpdateButtonState()
        {
            bool streaming = _streamCancellation != null;
            bool connected = _gpsPort != null && _gpsPort.IsOpen;
            bool controlsLocked = streaming || _connectingGps;
            int port;
            bool validPort = Int32.TryParse(_tcpPort.Text, out port) && port >= 1 && port <= 65535;

            _refresh.Enabled = !controlsLocked && !connected;
            _inputPort.Enabled = !controlsLocked && !connected;
            _baudRate.Enabled = !controlsLocked && !connected;
            _tcpPort.Enabled = !controlsLocked;
            _connect.Enabled = !controlsLocked && !connected && _inputPort.SelectedItem != null;
            _disconnect.Enabled = !controlsLocked && !streaming && connected;
            _switchToNmea.Enabled = !controlsLocked && !streaming && connected;
            _start.Enabled = !controlsLocked && !streaming && connected && validPort;
            _stop.Enabled = streaming;
        }

        private void ConnectGps(object sender, EventArgs e)
        {
            if (_connectingGps || _streamCancellation != null || (_gpsPort != null && _gpsPort.IsOpen))
                return;

            string input = _inputPort.SelectedItem as string;
            int baud;
            if (String.IsNullOrEmpty(input) ||
                !Int32.TryParse(_baudRate.SelectedItem as string, out baud))
            {
                SetStatus("Select a Bluetooth GPS input port and baud rate.");
                return;
            }

            _connectingGps = true;
            SetStatus("Opening " + input + " at " + baud + " baud to connect its Bluetooth serial service...");
            Log("Opening " + input + ". The app keeps the Bluetooth serial handle open until Disconnect GPS.");
            UpdateButtonState();

            Task.Run(delegate
            {
                SerialPort gps = CreatePort(input, baud);
                try
                {
                    gps.Open();
                }
                catch (UnauthorizedAccessException ex)
                {
                    gps.Dispose();
                    CompleteGpsConnect(input, null, ex);
                    return;
                }
                catch (IOException ex)
                {
                    gps.Dispose();
                    CompleteGpsConnect(input, null, ex);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    gps.Dispose();
                    CompleteGpsConnect(input, null, ex);
                    return;
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    gps.Dispose();
                    CompleteGpsConnect(input, null, ex);
                    return;
                }
                catch (ArgumentException ex)
                {
                    gps.Dispose();
                    CompleteGpsConnect(input, null, ex);
                    return;
                }
                catch (SecurityException ex)
                {
                    gps.Dispose();
                    CompleteGpsConnect(input, null, ex);
                    return;
                }

                CompleteGpsConnect(input, gps, null);
            });
        }

        private void CompleteGpsConnect(string input, SerialPort gps, Exception error)
        {
            bool posted = OnUi(delegate
            {
                _connectingGps = false;
                if (IsDisposed || !IsHandleCreated)
                {
                    ClosePort(gps);
                    return;
                }

                if (error != null)
                {
                    SetStatus("Could not connect to " + input + ": " + error.Message);
                    Log("GPS connection failed: " + error);
                    MessageBox.Show(this,
                        error.Message + Environment.NewLine + Environment.NewLine +
                        "Make sure the GPS is powered, paired, in range, and not open in another app.",
                        "GPS Bluetooth connection failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                else
                {
                    _gpsPort = gps;
                    SetStatus(input + " is open. Waiting for NMEA data to confirm the live link.");
                    Log(input + " opened at " + gps.BaudRate +
                        " baud. A successful port open does not prove that GPS data is arriving.");
                }
                UpdateButtonState();
            });
            if (!posted)
                ClosePort(gps);
        }

        private void DisconnectGps()
        {
            if (_streamCancellation != null)
                StopTcpStream();

            SerialPort gps = _gpsPort;
            _gpsPort = null;
            ClosePort(gps);
            if (gps != null)
            {
                SetStatus("GPS Bluetooth serial port disconnected.");
                Log("GPS Bluetooth serial port closed.");
            }
            UpdateButtonState();
        }

        private void SwitchToNmea(object sender, EventArgs e)
        {
            string input = _inputPort.SelectedItem as string;
            SerialPort gps = _gpsPort;
            if (String.IsNullOrEmpty(input) || gps == null || !gps.IsOpen)
            {
                SetStatus("Connect to the GPS before requesting NMEA mode.");
                return;
            }

            DialogResult answer = MessageBox.Show(
                this,
                "This sends the Garmin GPS 10x NMEA-mode command at 9600 baud. The receiver's output mode may change. Continue?",
                "Request NMEA mode",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
                return;

            try
            {
                StringBuilder response = new StringBuilder();
                int originalBaud = gps.BaudRate;
                try
                {
                    gps.BaudRate = 9600;
                    gps.DiscardInBuffer();
                    gps.Write(Gps10xNmeaModeCommand, 0, Gps10xNmeaModeCommand.Length);
                    DateTime deadline = DateTime.UtcNow.AddMilliseconds(1800);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (gps.BytesToRead > 0)
                            response.Append(gps.ReadExisting());
                        Thread.Sleep(50);
                    }
                }
                finally
                {
                    if (gps.IsOpen && gps.BaudRate != originalBaud)
                        gps.BaudRate = originalBaud;
                }

                string text = response.ToString();
                int sentenceStart = text.IndexOf('$');
                if (sentenceStart >= 0)
                {
                    int sentenceEnd = text.IndexOfAny(new[] { '\r', '\n' }, sentenceStart);
                    string sentence = sentenceEnd > sentenceStart
                        ? text.Substring(sentenceStart, sentenceEnd - sentenceStart)
                        : text.Substring(sentenceStart);
                    SetStatus("NMEA response observed after the mode-switch request.");
                    _nmeaStatus.Text = "NMEA monitor: " + sentence;
                    Log("NMEA-mode command sent; response observed: " + sentence);
                }
                else
                {
                    SetStatus("Mode-switch request sent; no NMEA response was confirmed.");
                    Log("No NMEA sentence observed. Check the GPS connection; power cycling may be needed to reconnect.");
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                ShowSerialError("GPS serial access failed", ex);
            }
            catch (IOException ex)
            {
                ShowSerialError("GPS serial I/O failed", ex);
            }
            catch (InvalidOperationException ex)
            {
                ShowSerialError("GPS serial operation failed", ex);
            }
            catch (TimeoutException ex)
            {
                ShowSerialError("Timed out waiting for the GPS", ex);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                ShowSerialError("Windows could not use the GPS port", ex);
            }
        }

        private void StartTcpStream(object sender, EventArgs e)
        {
            int port;
            if (_gpsPort == null || !_gpsPort.IsOpen)
            {
                SetStatus("Connect to the GPS before starting the NMEA TCP server.");
                return;
            }
            if (!Int32.TryParse(_tcpPort.Text, out port) || port < 1 || port > 65535)
            {
                SetStatus("Enter a TCP port from 1 to 65535.");
                return;
            }

            NmeaTcpServer server = new NmeaTcpServer(
                IPAddress.Loopback,
                port,
                message => OnUi(delegate { Log(message); }),
                count => OnUi(delegate
                {
                    if (_server != null)
                        SetStatus(count == 0
                            ? "TCP server listening on 127.0.0.1:" + port + "; waiting for OpenCPN."
                            : "OpenCPN connected to the NMEA TCP server.");
                }));

            try
            {
                server.Start();
            }
            catch (SocketException ex)
            {
                server.Dispose();
                ShowSerialError("Could not start the NMEA TCP server on port " + port, ex);
                return;
            }
            catch (InvalidOperationException ex)
            {
                server.Dispose();
                ShowSerialError("Could not start the NMEA TCP server", ex);
                return;
            }

            int baud = _gpsPort.BaudRate;
            _server = server;
            _streamCancellation = new CancellationTokenSource();
            Interlocked.Exchange(ref _gpsToNetworkBytes, 0);
            _nmeaMonitor.Reset();
            _nmeaStatus.Text = "NMEA monitor: waiting for GPS sentences.";
            _fixStatus.Text = "GPS fix: not reported yet.";
            _fixStatus.ForeColor = Color.DarkGoldenrod;
            CancellationToken token = _streamCancellation.Token;
            _gpsReadTask = Task.Run(() => ReadAndForwardGps(server, token));
            _statsTimer.Start();

            Log("Listening on 127.0.0.1:" + port + " at " + baud + " GPS baud.");
            Log("Configure OpenCPN as a TCP client, direction Input, address 127.0.0.1, port " + port + ".");
            SetStatus("TCP server listening on 127.0.0.1:" + port + "; waiting for OpenCPN.");
            UpdateButtonState();
        }

        private void ReadAndForwardGps(NmeaTcpServer server, CancellationToken token)
        {
            byte[] buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = _gpsPort.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (ObjectDisposedException ex)
                {
                    if (!token.IsCancellationRequested)
                        ReportGpsReadFailure(ex);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    if (!token.IsCancellationRequested)
                        ReportGpsReadFailure(ex);
                    return;
                }
                catch (IOException ex)
                {
                    if (!token.IsCancellationRequested)
                        ReportGpsReadFailure(ex);
                    return;
                }

                if (count <= 0)
                    continue;

                List<NmeaObservation> observations = _nmeaMonitor.Observe(buffer, count);
                foreach (NmeaObservation observation in observations)
                {
                    byte[] sentence = Encoding.ASCII.GetBytes(observation.Sentence + "\r\n");
                    server.Broadcast(sentence);
                    Interlocked.Add(ref _gpsToNetworkBytes, sentence.Length);
                    OnUi(delegate
                    {
                        _nmeaStatus.Text = "NMEA monitor: " + observation.Sentence;
                        _fixStatus.Text = observation.FixStatus;
                        _fixStatus.ForeColor = observation.HasFix.GetValueOrDefault()
                            ? Color.DarkGreen
                            : (observation.HasFix.HasValue ? Color.Firebrick : Color.DarkGoldenrod);
                    });
                }
            }
        }

        private void ReportGpsReadFailure(Exception ex)
        {
            OnUi(delegate
            {
                if (_streamCancellation == null)
                    return;
                Log("GPS serial read failed: " + ex);
                StopTcpStream();
                SetStatus("GPS serial read failed: " + ex.Message);
            });
        }

        private void StopTcpStream()
        {
            if (_streamCancellation == null)
                return;

            _statsTimer.Stop();
            CancellationTokenSource cancellation = _streamCancellation;
            NmeaTcpServer server = _server;
            Task readTask = _gpsReadTask;
            _streamCancellation = null;
            _server = null;
            _gpsReadTask = null;
            cancellation.Cancel();
            server.Dispose();
            SetStatus("NMEA TCP stream stopped. The Bluetooth GPS connection remains open.");
            Log("NMEA TCP server stopped; GPS Bluetooth connection remains open.");
            UpdateButtonState();

            if (readTask == null)
            {
                cancellation.Dispose();
                return;
            }

            readTask.ContinueWith(delegate(Task finished)
            {
                if (finished.IsFaulted && finished.Exception != null)
                {
                    AggregateException errors = finished.Exception.Flatten();
                    OnUi(delegate
                    {
                        foreach (Exception error in errors.InnerExceptions)
                            Log("GPS reader task failed: " + error);
                    });
                }
                cancellation.Dispose();
            });
        }

        private static SerialPort CreatePort(string name, int baud)
        {
            SerialPort port = new SerialPort(name, baud, Parity.None, 8, StopBits.One);
            port.Handshake = Handshake.None;
            port.DtrEnable = false;
            port.RtsEnable = false;
            port.ReadTimeout = 250;
            port.WriteTimeout = 10000;
            return port;
        }

        private void ClosePort(SerialPort port)
        {
            if (port == null)
                return;
            try
            {
                if (port.IsOpen)
                    port.Close();
                port.Dispose();
            }
            catch (IOException ex)
            {
                Log("Error closing serial port: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                Log("Error closing serial port: " + ex.Message);
            }
        }

        private void ShowSerialError(string title, Exception ex)
        {
            SetStatus(title + ": " + ex.Message);
            Log(title + ": " + ex);
            MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void SetStatus(string text)
        {
            _status.Text = text;
        }

        private void Log(string text)
        {
            if (_log.IsDisposed || !_log.IsHandleCreated)
                return;

            if (_log.InvokeRequired)
            {
                OnUi(delegate { Log(text); });
                return;
            }

            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
            if (_log.TextLength > 50000)
            {
                int removeThrough = _log.Text.IndexOf(Environment.NewLine, 10000, StringComparison.Ordinal);
                if (removeThrough > 0)
                    _log.Text = _log.Text.Substring(removeThrough + Environment.NewLine.Length);
            }
        }

        private bool OnUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
                return false;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(action);
                    return true;
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine("Could not post UI update: " + ex.Message);
                    return false;
                }
            }
            action();
            return true;
        }
    }

    internal sealed class NmeaTcpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Action<string> _log;
        private readonly Action<int> _clientCountChanged;
        private readonly object _sync = new object();
        private readonly List<ClientSession> _clients = new List<ClientSession>();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private Task _acceptTask;
        private int _stopped;

        public NmeaTcpServer(IPAddress address, int port, Action<string> log, Action<int> clientCountChanged)
        {
            _listener = new TcpListener(address, port);
            _log = log;
            _clientCountChanged = clientCountChanged;
        }

        public int Port
        {
            get { return ((IPEndPoint)_listener.LocalEndpoint).Port; }
        }

        public int ClientCount
        {
            get
            {
                lock (_sync)
                    return _clients.Count;
            }
        }

        public void Start()
        {
            _listener.Start();
            _acceptTask = Task.Run(new Func<Task>(AcceptClients));
        }

        public void Broadcast(byte[] sentence)
        {
            ClientSession[] clients;
            lock (_sync)
                clients = _clients.ToArray();

            foreach (ClientSession client in clients)
            {
                if (!client.Enqueue(sentence))
                {
                    Report("Disconnecting slow TCP client " + client.RemoteEndPoint +
                           " because its send queue is full.");
                    RemoveClient(client);
                }
            }
        }

        private async Task AcceptClients()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                TcpClient tcpClient = null;
                try
                {
                    tcpClient = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    tcpClient.NoDelay = true;
                    ClientSession session = new ClientSession(
                        tcpClient,
                        _cancellation.Token,
                        RemoveClient,
                        Report);

                    lock (_sync)
                    {
                        if (_cancellation.IsCancellationRequested)
                        {
                            session.Dispose();
                            return;
                        }
                        _clients.Add(session);
                    }

                    Report("OpenCPN TCP client connected: " + session.RemoteEndPoint);
                    PublishClientCount();
                    session.Start();
                }
                catch (ObjectDisposedException)
                {
                    if (tcpClient != null)
                        tcpClient.Close();
                    return;
                }
                catch (SocketException ex)
                {
                    if (tcpClient != null)
                        tcpClient.Close();
                    if (_cancellation.IsCancellationRequested)
                        return;
                    Report("TCP accept failed; stopping the TCP listener: " + ex.Message);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    if (tcpClient != null)
                        tcpClient.Close();
                    if (_cancellation.IsCancellationRequested)
                        return;
                    Report("TCP server stopped accepting clients: " + ex.Message);
                    return;
                }
            }
        }

        private void RemoveClient(ClientSession client)
        {
            bool removed;
            lock (_sync)
                removed = _clients.Remove(client);
            if (removed)
            {
                client.Dispose();
                Report("OpenCPN TCP client disconnected: " + client.RemoteEndPoint);
                PublishClientCount();
            }
        }

        private void PublishClientCount()
        {
            Action<int> callback = _clientCountChanged;
            if (callback != null)
                callback(ClientCount);
        }

        private void Report(string message)
        {
            Action<string> callback = _log;
            if (callback != null)
                callback(message);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;

            _cancellation.Cancel();
            _listener.Stop();
            ClientSession[] clients;
            lock (_sync)
            {
                clients = _clients.ToArray();
                _clients.Clear();
            }

            foreach (ClientSession client in clients)
                client.Dispose();
            if (clients.Length > 0)
                PublishClientCount();
            _cancellation.Dispose();
        }

        private sealed class ClientSession : IDisposable
        {
            private const int MaxQueuedSentences = 256;
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;
            private readonly CancellationTokenSource _cancellation;
            private readonly Action<ClientSession> _closed;
            private readonly Action<string> _log;
            private readonly object _sync = new object();
            private readonly Queue<byte[]> _queue = new Queue<byte[]>();
            private readonly SemaphoreSlim _available = new SemaphoreSlim(0);
            private Task _writerTask;
            private int _stopped;

            public ClientSession(TcpClient client, CancellationToken serverToken,
                Action<ClientSession> closed, Action<string> log)
            {
                _client = client;
                _stream = client.GetStream();
                _cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                _closed = closed;
                _log = log;
            }

            public string RemoteEndPoint
            {
                get
                {
                    try
                    {
                        return _client.Client.RemoteEndPoint == null
                            ? "unknown endpoint"
                            : _client.Client.RemoteEndPoint.ToString();
                    }
                    catch (ObjectDisposedException)
                    {
                        return "closed endpoint";
                    }
                    catch (SocketException)
                    {
                        return "unavailable endpoint";
                    }
                }
            }

            public void Start()
            {
                _writerTask = Task.Run(new Func<Task>(WriteQueuedSentences));
            }

            public bool Enqueue(byte[] sentence)
            {
                lock (_sync)
                {
                    if (_stopped != 0 || _queue.Count >= MaxQueuedSentences)
                        return false;
                    _queue.Enqueue(sentence);
                }
                _available.Release();
                return true;
            }

            private async Task WriteQueuedSentences()
            {
                CancellationToken token = _cancellation.Token;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await _available.WaitAsync(token).ConfigureAwait(false);
                        byte[] sentence;
                        lock (_sync)
                        {
                            if (_queue.Count == 0)
                                continue;
                            sentence = _queue.Dequeue();
                        }
                        await _stream.WriteAsync(sentence, 0, sentence.Length, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException ex)
                {
                    if (!token.IsCancellationRequested)
                        _log("TCP client write failed (" + RemoteEndPoint + "): " + ex.Message);
                    _closed(this);
                }
                catch (ObjectDisposedException)
                {
                    if (!token.IsCancellationRequested)
                        _closed(this);
                }
                catch (InvalidOperationException ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        _log("TCP client writer stopped (" + RemoteEndPoint + "): " + ex.Message);
                        _closed(this);
                    }
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _stopped, 1) != 0)
                    return;
                _cancellation.Cancel();
                try
                {
                    _client.Close();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    internal sealed class NmeaObservation
    {
        public string Sentence { get; private set; }
        public string FixStatus { get; private set; }
        public bool? HasFix { get; private set; }

        public NmeaObservation(string sentence, string fixStatus, bool? hasFix)
        {
            Sentence = sentence;
            FixStatus = fixStatus;
            HasFix = hasFix;
        }
    }

    internal sealed class NmeaLineMonitor
    {
        private readonly StringBuilder _line = new StringBuilder();
        private bool _collecting;
        private int? _ggaFixQuality;
        private int? _satellites;
        private char? _rmcStatus;

        public void Reset()
        {
            _line.Length = 0;
            _collecting = false;
            _ggaFixQuality = null;
            _satellites = null;
            _rmcStatus = null;
        }

        public List<NmeaObservation> Observe(byte[] bytes, int count)
        {
            List<NmeaObservation> observations = new List<NmeaObservation>();
            for (int i = 0; i < count; i++)
            {
                byte value = bytes[i];
                if (value == (byte)'$' || value == (byte)'!')
                {
                    _line.Length = 0;
                    _line.Append((char)value);
                    _collecting = true;
                }
                else if (!_collecting)
                {
                    continue;
                }
                else if (value == (byte)'\r' || value == (byte)'\n')
                {
                    if (_line.Length > 5)
                    {
                        string sentence = _line.ToString();
                        UpdateFixState(sentence);
                        observations.Add(CreateObservation(sentence));
                    }
                    _line.Length = 0;
                    _collecting = false;
                }
                else if (value >= 0x20 && value <= 0x7e && _line.Length < 160)
                {
                    _line.Append((char)value);
                }
                else
                {
                    _line.Length = 0;
                    _collecting = false;
                }
            }
            return observations;
        }

        private void UpdateFixState(string sentence)
        {
            string[] fields = sentence.Split(',');
            if (fields.Length == 0)
                return;

            if (fields[0].EndsWith("GGA", StringComparison.Ordinal))
            {
                int quality;
                if (fields.Length > 6 && Int32.TryParse(fields[6], out quality))
                    _ggaFixQuality = quality;

                int satellites;
                if (fields.Length > 7 && Int32.TryParse(fields[7], out satellites))
                    _satellites = satellites;
            }
            else if (fields[0].EndsWith("RMC", StringComparison.Ordinal) && fields.Length > 2 &&
                     fields[2].Length == 1)
            {
                _rmcStatus = fields[2][0];
            }
        }

        private NmeaObservation CreateObservation(string sentence)
        {
            if ((_ggaFixQuality.HasValue && _ggaFixQuality.Value == 0) || _rmcStatus == 'V')
            {
                string detail = _satellites.HasValue
                    ? "GGA quality " + _ggaFixQuality.GetValueOrDefault() +
                      ", " + _satellites.GetValueOrDefault() + " satellites"
                    : "receiver reports invalid navigation data";
                return new NmeaObservation(sentence, "GPS fix: NO FIX (" + detail + ").", false);
            }

            if (_ggaFixQuality.HasValue && _ggaFixQuality.Value > 0 && _rmcStatus == 'A')
            {
                string satelliteCount = _satellites.HasValue
                    ? ", " + _satellites.GetValueOrDefault() + " satellites"
                    : String.Empty;
                return new NmeaObservation(sentence,
                    "GPS fix: receiver reports a valid fix (GGA quality " +
                    _ggaFixQuality.GetValueOrDefault() + satelliteCount + ").", true);
            }

            return new NmeaObservation(sentence, "GPS fix: NMEA received; fix status is not confirmed yet.", null);
        }
    }

    internal static class PortPairCatalog
    {
        public static bool IsComPort(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !value.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                return false;
            int number;
            return Int32.TryParse(value.Substring(3), out number) && number > 0;
        }

        public static int PortNumber(string value)
        {
            int number;
            return Int32.TryParse(value.Substring(3), out number) ? number : Int32.MaxValue;
        }
    }

    internal static class BridgeSelfTest
    {
        public static int Run()
        {
            if (!TestNmeaFixParsing())
                return 1;
            if (!TestTcpNmeaDelivery())
                return 1;

            Console.WriteLine("PASS: NMEA parser and loopback TCP delivery.");
            return 0;
        }

        private static bool TestNmeaFixParsing()
        {
            NmeaLineMonitor monitor = new NmeaLineMonitor();
            byte[] noFix = Encoding.ASCII.GetBytes(
                "$GPGGA,000137.060,,,,,0,00,,,M,0.0,M,,0000*55\r\n" +
                "$GPRMC,000137.060,V,,,,,,,130806,,,N*42\r\n");
            List<NmeaObservation> observations = monitor.Observe(noFix, noFix.Length);
            if (observations.Count != 2 || observations[1].HasFix != false)
            {
                Console.Error.WriteLine("FAIL: NMEA no-fix sentences were not identified.");
                return false;
            }

            monitor.Reset();
            byte[] validFix = Encoding.ASCII.GetBytes(
                "$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47\r\n" +
                "$GPRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W*6A\r\n");
            observations = monitor.Observe(validFix, validFix.Length);
            if (observations.Count != 2 || observations[1].HasFix != true)
            {
                Console.Error.WriteLine("FAIL: valid NMEA fix sentences were not identified.");
                return false;
            }
            Console.WriteLine("PASS: NMEA monitor distinguishes no-fix and valid-fix sentences.");
            return true;
        }

        private static bool TestTcpNmeaDelivery()
        {
            NmeaTcpServer server = null;
            TcpClient client = null;
            try
            {
                TaskCompletionSource<int> connected = new TaskCompletionSource<int>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                server = new NmeaTcpServer(IPAddress.Loopback, 0, null, count =>
                {
                    if (count > 0)
                        connected.TrySetResult(count);
                });
                server.Start();

                client = new TcpClient();
                Task connectTask = client.ConnectAsync(IPAddress.Loopback, server.Port);
                if (!connectTask.Wait(3000) || !connected.Task.Wait(3000))
                    throw new TimeoutException("Loopback TCP client did not connect to the bridge.");

                byte[] expected = Encoding.ASCII.GetBytes("$GPRMC,TEST*00\r\n");
                server.Broadcast(expected);
                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 3000;
                byte[] received = new byte[expected.Length];
                int offset = 0;
                while (offset < received.Length)
                    offset += stream.Read(received, offset, received.Length - offset);

                if (!expected.SequenceEqual(received))
                {
                    Console.Error.WriteLine("FAIL: TCP server altered the NMEA payload.");
                    return false;
                }

                Console.WriteLine("PASS: NMEA sentence delivered unchanged over loopback TCP.");
                return true;
            }
            catch (AggregateException ex)
            {
                Console.Error.WriteLine("FAIL: TCP loopback test failed: " + ex.Flatten().InnerException);
                return false;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("FAIL: TCP loopback test failed: " + ex.Message);
                return false;
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine("FAIL: TCP loopback test failed: " + ex.Message);
                return false;
            }
            catch (TimeoutException ex)
            {
                Console.Error.WriteLine("FAIL: TCP loopback test failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (client != null)
                    client.Close();
                if (server != null)
                    server.Dispose();
            }
        }
    }
}
