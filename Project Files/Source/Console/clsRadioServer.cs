using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace Thetis
{
    internal class clsRadioServer
    {
        private const int SERVER_PROTOCOL = 102;
        private const int MAX_SPECTRUM_FRAME_RATE = 30;

        private class radioClient
        {
            public TcpClient client;
            public bool is_new_connection;
            public radioClient(TcpClient c)
            {
                is_new_connection = true;
                client = c;
            }
        }
        public class RXdata
        {
            public int rx;
            public int spectrum_width;
            public bool spectrum_data_updated;
            public float[] spectrum_data;
            //public DateTime last_copy;
            public Color[] gradient;
            public float[] gradient_positions;
            public bool gradient_updated;
            public bool radio_data_updated;
            public byte decimation;

            public HiPerfTimer stopwatch;
            public double stopwatch_remainder;

            public RXdata(int rx)
            {
                this.rx = rx;

                stopwatch = new HiPerfTimer();
                stopwatch.Reset();
                stopwatch_remainder = 0;

                spectrum_data_updated = false;
                spectrum_width = -1;
                decimation = 1;

                gradient_updated = false;
                radio_data_updated = false;

                //last_copy = DateTime.MinValue;
            }
        }

        private TcpListener _listener;
        private Thread _listen_thread;
        private Thread _send_thread;
        private Thread _receive_thread;
        private volatile bool _listening;
        private volatile bool _send_running;
        private List<radioClient> _clients;
        private Console _console;
        private string _password;

        private object _clients_lock = new object();

        /*
            48291537.
            73920146.
            26834759.
            90158273
            31570948
            65483012
            12769485
            84630159
            50392768
            79218435
         */

        private const int SPECTRUM_FRAME_ID = 48291537;
        private const int GRADIENT_FRAME_ID = 73920146;
        private const int RADIODATA_FRAME_ID = 26834759;     

        private RXdata _rx1Data;
        private RXdata _rx2Data;

        public clsRadioServer(Console c, string ip, int port, string password)
        {
            _console = c;
            _password = password;

            addDelegates();

            IPAddress bindAddress;
            if (string.IsNullOrEmpty(ip))
            {
                bindAddress = IPAddress.Any;
            }
            else if (!IPAddress.TryParse(ip, out bindAddress))
            {
                throw new ArgumentException("Invalid IP address", nameof(ip));
            }

            _listener = new TcpListener(bindAddress, port);
            _rx1Data = new RXdata(1);
            _rx2Data = new RXdata(2);
            _clients = new List<radioClient>();
        }

        private void addDelegates()
        {
            if (_console == null) return;

            _console.AttenuatorDataChangedHandlers += OnAttenuatorDataChanged;
            _console.PreampModeChangedHandlers += OnPreampModeChanged;
            _console.MeterCalOffsetChangedHandlers += OnMeterCalOffsetChanged;
            _console.DisplayOffsetChangedHandlers += OnDisplayOffsetChanged;
            _console.XvtrGainOffsetChangedHandlers += OnXvtrGainOffsetChanged;
            _console.Rx6mOffsetChangedHandlers += OnRx6mOffsetChanged;
            _console.FSPChangedHandlers += OnFpsChanged;
            _console.DisplayDecimationChangedHanders += OnDecimationChanged;
        }
        private void removeDelegates()
        {
            if (_console == null) return;

            _console.AttenuatorDataChangedHandlers -= OnAttenuatorDataChanged;
            _console.PreampModeChangedHandlers -= OnPreampModeChanged;
            _console.MeterCalOffsetChangedHandlers -= OnMeterCalOffsetChanged;
            _console.DisplayOffsetChangedHandlers -= OnDisplayOffsetChanged;
            _console.XvtrGainOffsetChangedHandlers -= OnXvtrGainOffsetChanged;
            _console.Rx6mOffsetChangedHandlers -= OnRx6mOffsetChanged;
            _console.FSPChangedHandlers -= OnFpsChanged;
            _console.DisplayDecimationChangedHanders -= OnDecimationChanged;
        }
        //delgates
        private void OnDecimationChanged(int oldDec, int newDec)
        {
            _rx1Data.decimation = (byte)newDec;
            _rx2Data.decimation = (byte)newDec;

            updateRadioData();
        }
        private void OnAttenuatorDataChanged(int rx, int oldAtt, int newAtt)
        {
            updateRadioData();
        }
        private void OnPreampModeChanged(int rx, PreampMode oldPreampMode, PreampMode newPreampMode)
        {
            updateRadioData();
        }
        private void OnMeterCalOffsetChanged(int rx, float oldCal, float newCal)
        {
            updateRadioData();
        }
        private void OnDisplayOffsetChanged(int rx, float oldCal, float newCal)
        {
            updateRadioData();
        }
        private void OnXvtrGainOffsetChanged(int rx, float oldCal, float newCal)
        {
            updateRadioData();
        }
        private void OnRx6mOffsetChanged(int rx, float oldCal, float newCal)
        {
            updateRadioData();
        }
        private void OnFpsChanged(int oldFps, int newFps)
        {
            updateRadioData();
        }
        //

        private void updateRadioData()
        {
            SendRadioData(1);
            SendRadioData(2);
        }
        public void StartListening()
        {
            if (_listening) return;
            _listener.Start();
            _listening = true;
            _send_running = true;

            _listen_thread = new Thread(listen_for_clients);
            _listen_thread.IsBackground = true;
            _listen_thread.Start();

            _send_thread = new Thread(send_loop);
            _send_thread.IsBackground = true;
            _send_thread.Start();

            _receive_thread = new Thread(receive_loop);
            _receive_thread.IsBackground = true;
            _receive_thread.Start();
        }

        public void StopListening()
        {
            _listening = false;
            _send_running = false;
            try 
            { 
                _listener.Stop(); 
            } 
            catch { }

            if (_send_thread != null && _send_thread.IsAlive)
            {
                _send_thread.Join();
            }

            if (_receive_thread != null && _receive_thread.IsAlive)
            {
                _receive_thread.Join();
            }

            lock (_clients_lock)
            {
                foreach (radioClient c in _clients) 
                { 
                    try 
                    {
                        c.client.Close();
                    } 
                    catch { } 
                }
                _clients.Clear();
            }
        }

        public bool SendData(int rx, float[] pixel_data, int pixel_width)
        {
            RXdata rxdata = rx == 1 ? _rx1Data : _rx2Data;
            if (rxdata.spectrum_data_updated) return false;

            double elapsed = rxdata.stopwatch.ElapsedMsec;
            double time_per_frame = 1000.0 / (double)MAX_SPECTRUM_FRAME_RATE;

            if (elapsed + rxdata.stopwatch_remainder < time_per_frame) return false;

            rxdata.stopwatch_remainder = elapsed - time_per_frame;// Math.Max(0, elapsed - time_per_frame);

            if (pixel_width != rxdata.spectrum_width)
            {
                rxdata.spectrum_data = new float[pixel_width];
                rxdata.spectrum_width = pixel_width;
                rxdata.radio_data_updated = true;
            }

            unsafe
            {
                fixed (void* rptr = &pixel_data[0])
                fixed (void* wptr = &rxdata.spectrum_data[0])
                    Win32.memcpy(wptr, rptr, pixel_width * sizeof(float));
            }

            //rxdata.last_copy = DateTime.UtcNow;
            rxdata.stopwatch.Reset();
            rxdata.spectrum_data_updated = true;
            return true;
        }
        public void SendGradient(int rx, float[] positions, Color[] gradColours)
        {
            RXdata rxdata = rx == 1 ? _rx1Data : _rx2Data;

            rxdata.gradient_positions = (float[])positions.Clone();
            rxdata.gradient = (Color[])gradColours.Clone();
            rxdata.gradient_updated = true;
        }
        public void SendRadioData(int rx)
        {
            RXdata rxdata = rx == 1 ? _rx1Data : _rx2Data;

            rxdata.radio_data_updated = true;
        }
        private void listen_for_clients()
        {
            while (_listening)
            {
                try
                {
                    radioClient radClient = new radioClient(_listener.AcceptTcpClient());
                    lock (_clients_lock) 
                    {
                        NetworkStream stream = radClient.client.GetStream();

                        // authenticate
                        // server sends down a nonce, client uses that to generate a hash based on sha256 and send to server
                        // server regenerates its own hash from its pawword and the nonce it sent
                        // if the same as the client hash received then connection is valid
                        byte[] password_bytes = Encoding.UTF8.GetBytes(_password);
                        byte[] nonce = new byte[16];
                        RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
                        rng.GetBytes(nonce);
                        stream.Write(nonce, 0, nonce.Length);

                        byte[] received_hmac = new byte[32];
                        int total_read = 0;
                        while (total_read < 32)
                        {
                            int read = stream.Read(received_hmac, total_read, 32 - total_read);
                            if (read <= 0)
                            {
                                radClient.client.Close();
                                continue;
                            }
                            total_read += read;
                        }

                        HMACSHA256 hmac = new HMACSHA256(password_bytes);
                        byte[] expected_hmac = hmac.ComputeHash(nonce);

                        bool authorised = true;
                        for (int i = 0; i < 32; i++)
                        {
                            if (received_hmac[i] != expected_hmac[i])
                            {
                                authorised = false;
                                break;
                            }
                        }
                        byte[] response = Encoding.UTF8.GetBytes(authorised ? "_OK_" : "FAIL");
                        stream.Write(response, 0, response.Length);
                        //

                        if (authorised)
                        {
                            _clients.Add(radClient);
                        }
                    }
                }
                catch { }
            }
        }

        private void send_loop()
        {
            while (_send_running)
            {
                bool sleep = true;
                for (int rx = 1; rx <= 2; rx++)
                {
                    RXdata rxdata = rx == 1 ? _rx1Data : _rx2Data;

                    if (rxdata.spectrum_data_updated || rxdata.gradient_updated)
                    {
                        lock (_clients_lock)
                        {
                            for (int i = _clients.Count - 1; i >= 0; i--)
                            {
                                radioClient radClient = _clients[i];
                                TcpClient client = radClient.client;
                                try
                                {
                                    NetworkStream stream = client.GetStream();
                                    byte[] data;

                                    if (rxdata.radio_data_updated || radClient.is_new_connection) // is_new_connection - a new connection, this always goes out
                                    {
                                        if (_console == null) continue;

                                        sleep = false;

                                        //frame id
                                        data = SerialiseToBytes<int>(RADIODATA_FRAME_ID);
                                        stream.Write(data, 0, data.Length);

                                        //protocol
                                        data = SerialiseToBytes<int>(SERVER_PROTOCOL);
                                        stream.Write(data, 0, data.Length);

                                        //rx1 calibration
                                        data = SerialiseToBytes<float>(_console.RXOffset(1));
                                        stream.Write(data, 0, data.Length);

                                        //rx2 calibration
                                        data = SerialiseToBytes<float>(_console.RXOffset(2));
                                        stream.Write(data, 0, data.Length);

                                        //spectrum frame rate, which is limited to MAX_SPECTRUM_FRAME_RATE
                                        int frame_rate = (int)Math.Min(_console.DisplayFPS, MAX_SPECTRUM_FRAME_RATE);
                                        data = SerialiseToBytes<int>(frame_rate);
                                        stream.Write(data, 0, data.Length);

                                        //spectrum width
                                        data = SerialiseToBytes<int>(rxdata.spectrum_width);
                                        stream.Write(data, 0, data.Length);

                                        //spectrum decimation
                                        data = SerialiseToBytes<byte>(rxdata.decimation);
                                        stream.Write(data, 0, data.Length);

                                        stream.Flush();
                                    }

                                    if (rxdata.gradient_updated || radClient.is_new_connection) // is_new_connection - a new connection, this always goes out
                                    {
                                        sleep = false;

                                        //frame id
                                        data = SerialiseToBytes<int>(GRADIENT_FRAME_ID);
                                        stream.Write(data, 0, data.Length);

                                        //rx
                                        data = SerialiseToBytes<int>(rxdata.rx);
                                        stream.Write(data, 0, data.Length);

                                        //total colours
                                        data = SerialiseToBytes<int>(rxdata.gradient.Length);
                                        stream.Write(data, 0, data.Length);

                                        //colour positions
                                        for (int kk = 0; kk < rxdata.gradient_positions.Length; kk++)
                                        {
                                            data = SerialiseToBytes<float>(rxdata.gradient_positions[kk]);
                                            stream.Write(data, 0, data.Length);
                                        }

                                        //colours
                                        int count = rxdata.gradient.Length;
                                        byte[] buffer = new byte[count * 4];
                                        for (int k = 0; k < count; k++)
                                        {
                                            Color colour = rxdata.gradient[k];
                                            buffer[k * 4] = colour.R;
                                            buffer[k * 4 + 1] = colour.G;
                                            buffer[k * 4 + 2] = colour.B;
                                            buffer[k * 4 + 3] = colour.A;
                                        }
                                        stream.Write(buffer, 0, buffer.Length);

                                        stream.Flush();
                                    }

                                    if (rxdata.spectrum_data_updated && !radClient.is_new_connection) // only output when the other data has gone
                                    {
                                        sleep = false;

                                        //frame id
                                        data = SerialiseToBytes<int>(SPECTRUM_FRAME_ID);
                                        stream.Write(data, 0, data.Length);

                                        //rx
                                        data = SerialiseToBytes<int>(rxdata.rx);
                                        stream.Write(data, 0, data.Length);

                                        //spec
                                        int decimated_width = rxdata.spectrum_width / rxdata.decimation;
                                        int byteCount = decimated_width * sizeof(float);
                                        byte[] buffer = new byte[byteCount];
                                        unsafe
                                        {
                                            fixed (void* rptr = &rxdata.spectrum_data[0])
                                            fixed (void* wptr = &buffer[0])
                                                Win32.memcpy(wptr, rptr, byteCount);
                                        }

                                        using (MemoryStream ms = new MemoryStream())
                                        {
                                            using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress))
                                            {
                                                gz.Write(buffer, 0, buffer.Length);
                                                gz.Flush();
                                            }

                                            byte[] compressed = ms.ToArray();

                                            //len
                                            data = SerialiseToBytes<int>(compressed.Length);
                                            stream.Write(data, 0, data.Length);

                                            //compressed data
                                            stream.Write(compressed, 0, compressed.Length);
                                        }

                                        //int decimated_width = (int)(rxdata.spectrum_width / (float)rxdata.decimation);
                                        //int payloadLength = decimated_width * sizeof(float);
                                        //byte[] payload = new byte[payloadLength];
                                        //unsafe
                                        //{
                                        //    fixed (void* rptr = &rxdata.spectrum_data[0])
                                        //    fixed (void* wptr = &payload[0])
                                        //        Win32.memcpy(wptr, rptr, payloadLength);
                                        //}
                                        //stream.Write(payload, 0, payloadLength);

                                        stream.Flush();
                                    }

                                    if (radClient.is_new_connection) radClient.is_new_connection = false;
                                }
                                catch (Exception e)
                                {
                                    try
                                    {
                                        client.Close();
                                    }
                                    catch
                                    {
                                    }
                                    _clients.RemoveAt(i);
                                }
                            }
                        }
                        if (rxdata.spectrum_data_updated) rxdata.spectrum_data_updated = false;
                        if (rxdata.gradient_updated) rxdata.gradient_updated = false;
                        if (rxdata.radio_data_updated) rxdata.radio_data_updated = false;
                    }
                }
                if(sleep) Thread.Sleep(1);
            }
        }

        public byte[] SerialiseToBytes<T>(T instance) where T : struct
        {
            //int byteCount = Marshal.SizeOf(typeof(T));
            int byteCount;
            unsafe
            {
                byteCount = sizeof(T);
            }
            byte[] data = new byte[byteCount];
            T[] buffer = new T[1];
            buffer[0] = instance;
            Buffer.BlockCopy(buffer, 0, data, 0, byteCount);
            return data;
        }

        private void receive_loop()
        {
            while (_listening)
            {
                lock (_clients_lock)
                {
                    for (int i = _clients.Count - 1; i >= 0; i--)
                    {
                        radioClient radClient = _clients[i];
                        TcpClient client = radClient.client;
                        try
                        {
                            NetworkStream stream = client.GetStream();
                            if (!stream.DataAvailable) continue;

                            byte[] idBuffer = new byte[4];
                            int headerBytesRead = stream.Read(idBuffer, 0, idBuffer.Length);
                            if (headerBytesRead < 4) continue;

                            int frameId = BitConverter.ToInt32(idBuffer, 0);
                            switch (frameId)
                            {
                                default:
                                    break;
                            }
                        }
                        catch (Exception)
                        {
                            client.Close();
                            _clients.RemoveAt(i);
                        }
                    }
                }
                Thread.Sleep(1);
            }
        }
    }
}
