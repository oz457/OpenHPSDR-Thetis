using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Diagnostics;

namespace Thetis
{
    internal class clsRadioServer
    {
        private class radioClient
        {
            public TcpClient client;
            public bool is_new;
            public radioClient(TcpClient c)
            {
                is_new = true;
                client = c;
            }
        }
        public class RXdata
        {
            public int rx;
            public int spectrum_width;
            public bool spectrum_data_updated;
            public float[] spectrum_data;
            public DateTime last_copy;
            public Color[] gradient;
            public float[] gradient_positions;
            public bool gradient_updated;
            public bool radio_data_updated;

            public RXdata(int rx)
            {
                this.rx = rx;

                spectrum_data_updated = false;
                spectrum_width = -1;

                gradient_updated = false;
                radio_data_updated = false;

                last_copy = DateTime.MinValue;
            }
        }

        private TcpListener _listener;
        private Thread _listen_thread;
        private Thread _send_thread;
        private volatile bool _listening;
        private volatile bool _send_running;
        private List<radioClient> _clients;
        private Console _console;

        private object _clients_lock = new object();

        /*
            48291537
            73920146
            26834759
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

        public clsRadioServer(Console c, string ip, int port)
        {
            _console = c;

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
        }

        public void StopListening()
        {
            _listening = false;
            _send_running = false;
            try { _listener.Stop(); } catch { }
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

            if (rxdata.spectrum_data_updated || (DateTime.UtcNow - rxdata.last_copy).TotalMilliseconds < 32) return false;

            if(pixel_width != rxdata.spectrum_width)
            {
                rxdata.spectrum_data = new float[pixel_width];
                rxdata.spectrum_width = pixel_width;
            }

            unsafe
            {
                fixed (void* rptr = &pixel_data[0])
                fixed (void* wptr = &rxdata.spectrum_data[0])
                    Win32.memcpy(wptr, rptr, pixel_width * sizeof(float));
            }

            rxdata.last_copy = DateTime.UtcNow;
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
                    radioClient client = new radioClient(_listener.AcceptTcpClient());
                    lock (_clients_lock) 
                    { 
                        _clients.Add(client); 
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

                                    if (rxdata.spectrum_data_updated)
                                    {
                                        sleep = false;

                                        //frame id
                                        data = SerialiseToBytes<int>(SPECTRUM_FRAME_ID);
                                        stream.Write(data, 0, data.Length);

                                        //rx
                                        data = SerialiseToBytes<int>(rxdata.rx);
                                        stream.Write(data, 0, data.Length);

                                        //spec width
                                        data = SerialiseToBytes<int>(rxdata.spectrum_width);
                                        stream.Write(data, 0, data.Length);

                                        //spec
                                        int payloadLength = rxdata.spectrum_width * Marshal.SizeOf(typeof(float));
                                        byte[] payload = new byte[payloadLength];
                                        unsafe
                                        {
                                            fixed (void* rptr = &rxdata.spectrum_data[0])
                                            fixed (void* wptr = &payload[0])
                                                Win32.memcpy(wptr, rptr, payloadLength);
                                        }
                                        stream.Write(payload, 0, payloadLength);

                                        stream.Flush();
                                    }

                                    if (rxdata.gradient_updated || radClient.is_new)
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
                                        for(int kk = 0; kk < rxdata.gradient_positions.Length; kk++)
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

                                    if (rxdata.radio_data_updated || radClient.is_new)
                                    {
                                        if (_console == null) continue;

                                        sleep = false;

                                        //frame id
                                        data = SerialiseToBytes<int>(RADIODATA_FRAME_ID);
                                        stream.Write(data, 0, data.Length);

                                        //rx1 calibration
                                        data = SerialiseToBytes<float>(_console.RXOffset(1));
                                        stream.Write(data, 0, data.Length);

                                        //rx2 calibration
                                        data = SerialiseToBytes<float>(_console.RXOffset(2));
                                        stream.Write(data, 0, data.Length);
                                    }

                                    if (radClient.is_new) radClient.is_new = false;
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
            int byteCount = Marshal.SizeOf(typeof(T));
            byte[] data = new byte[byteCount];
            T[] buffer = new T[1];
            buffer[0] = instance;
            Buffer.BlockCopy(buffer, 0, data, 0, byteCount);
            return data;
        }
    }
}
