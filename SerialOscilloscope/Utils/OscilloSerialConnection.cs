using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SerialOscilloscope.Utils
{
    public class OscilloSerialConnection
    {
        // Constructor and Finalizer
        public OscilloSerialConnection()
        {
            _port = new SerialPort();
        }
        ~OscilloSerialConnection()
        {
            ClosePort();
        }

        // Fields and Properties
        private SerialPort _port;
        public string PortName { get; set; } = "COM1";
        public int PortBaud { get; set; } = 250000;
        public int PortReadTimeout { get; set; } = 2000;
        public int PortWriteTimeout { get; set; } = 100;

        private EventWaitHandle _waitPortReceive = new AutoResetEvent(true);
        private Thread _revThread;
        public bool IsReceiving { get; set; } = false;


        // Methods
        public static string[] GetAvailablePort()
        {
            return SerialPort.GetPortNames();
        }

        public bool IsOpen()
        {
            return _port.IsOpen;
        }

        public void SetupPort()
        {
            if (_port.IsOpen)
            {
                ClosePort();
            }
            _port.PortName = PortName;
            _port.BaudRate = PortBaud;
            _port.ReadTimeout = PortReadTimeout;
            _port.WriteTimeout = PortWriteTimeout;
            _port.DataReceived += (sender, args) =>
            {
                _waitPortReceive.Set();
            };
        }

        public bool OpenPort()
        {
            try
            {
                if (_port != null && !_port.IsOpen)
                {
                    _port.Open();
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                // throw new Exception(ex.Message);
                return false;
            }

        }

        public bool ClosePort()
        {
            try
            {
                if (_port != null && _port.IsOpen)
                {
                    IsReceiving = false;
                    _port.Close();
                    _port.Dispose();
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                // throw new Exception(ex.Message);
                return false;
            }
        }

        public bool SendData(byte[] byteData, int length)
        {
            try
            {
                if (_port != null && _port.IsOpen)
                {
                    _port.Write(byteData, 0, length);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                // throw new Exception(ex.Message);
                return false;
            }

        }

        public int ReceiveData(byte[] bytedata, int offset, int conut)
        {
            try
            {
                if (_port != null && _port.IsOpen)
                {
                    int readLen = _port.Read(bytedata, offset, conut);
                    return readLen;
                }
                return 0;
            }
            catch (Exception ex)
            {
                // throw new Exception(ex.Message);
                return 0;
            }

        }

        public bool SendConfigCmd(uint[] addrs, int scopeMode, int[] dataTypes, int[] floatScales)
        {
            if (!_port.IsOpen) return false;

            byte[] configData = new byte[50];
            // CommandCode-Addresses-(ScopeMode-DataTypes-DataScale)-CheckSum
            int i = 0;
            configData[i++] = 0xFF;
            configData[i++] = 0x10;

            i += DataPack((byte)(addrs[0] >> 24 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[0] >> 16 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[0] >> 8 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[0] & 0xFF), configData, i);

            i += DataPack((byte)(addrs[1] >> 24 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[1] >> 16 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[1] >> 8 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[1] & 0xFF), configData, i);

            i += DataPack((byte)(addrs[2] >> 24 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[2] >> 16 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[2] >> 8 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[2] & 0xFF), configData, i);

            i += DataPack((byte)(addrs[3] >> 24 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[3] >> 16 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[3] >> 8 & 0xFF), configData, i);
            i += DataPack((byte)(addrs[3] & 0xFF), configData, i);

            uint u32CfgCode = 0;
            if (scopeMode == 1)
            {
                // 0：滚动模式；1：标准模式
                u32CfgCode |= 0x80000000;
            }
            if (dataTypes[0] == 2)
            {
                // 0：uint16；1：int16；2：float32
                u32CfgCode |= 0x00010000;
            }
            if (dataTypes[1] == 2)
            {
                u32CfgCode |= 0x00020000;
            }
            if (dataTypes[2] == 2)
            {
                u32CfgCode |= 0x00040000;
            }
            if (dataTypes[3] == 2)
            {
                u32CfgCode |= 0x00080000;
            }

            u32CfgCode |= (uint)(floatScales[0] & 0xF);
            u32CfgCode |= (uint)((floatScales[1] & 0xF) << 4);
            u32CfgCode |= (uint)((floatScales[2] & 0xF) << 8);
            u32CfgCode |= (uint)((floatScales[3] & 0xF) << 12);

            i += DataPack((byte)(u32CfgCode >> 24 & 0xFF), configData, i);
            i += DataPack((byte)(u32CfgCode >> 16 & 0xFF), configData, i);
            i += DataPack((byte)(u32CfgCode >> 8 & 0xFF), configData, i);
            i += DataPack((byte)(u32CfgCode & 0xFF), configData, i);

            byte checkSum = GetCheckSum(configData, 0, i);
            i += DataPack(checkSum, configData, i);

            // 发送指令，并期望返回相同内容
            bool revFlag = true;
            bool revSuccess = false;
            var revThread = new Thread(() =>
            {
                _waitPortReceive.Reset();
                int revLen;
                byte[] revBuf = new byte[50];
                while (revFlag)
                {
                    _waitPortReceive.WaitOne(500);
                    revLen = ReceiveData(revBuf, 0, 30);
                    if (revLen == 23)
                    {
                        revFlag = false;
                        revSuccess = true;
                    }
                }
            });
            revThread.Start();


            for (int retry = 0; retry < 5; ++retry)
            {
                if (revSuccess) break;
                SendData(configData, i);
                Thread.Sleep(100);
            }

            revFlag = false;
            revThread.Join();

            if (!revSuccess) ClosePort();

            return revSuccess;
        }

        public bool SendStartCmd()
        {
            byte[] cmdData = new byte[3];
            cmdData[0] = 0xFF;
            cmdData[1] = 0x02;
            cmdData[2] = 0x01;  // checksum
            return SendData(cmdData, 3);
        }

        public bool SendStopCmd()
        {
            byte[] cmdData = new byte[3];
            cmdData[0] = 0xFF;
            cmdData[1] = 0x03;
            cmdData[2] = 0x02;   // checksum
            return SendData(cmdData, 3);
        }

        public void StartReceivingThread(IList<IProducerConsumerCollection<ushort>> CHBuffers)
        {

            _revThread = new Thread(() =>
            {
                Thread.Sleep(100);
                ReceiveToChanelBuffers(CHBuffers);
            });
            _revThread.Start();
        }

        public void StopReceivingThread()
        {
            IsReceiving = false;
            for (int i = 0; i < 5; i++)
            {
                SendStopCmd();
                Thread.Sleep(50);
            }
            ClosePort();
            _revThread?.Join();
        }

        public void ReceiveToChanelBuffers(IList<IProducerConsumerCollection<ushort>> CHBuffers)
        {
            _waitPortReceive.Reset();
            IsReceiving = true;
            byte[] revBuf = new byte[4096];
            int frameDataLen = 2 * CHBuffers.Count;    // 一个数据帧最少有3个字节

            int iRemain = 0;
            int revLen = 0;
            int revErrCount = 0;
            do
            {
                //_waitPortReceive.WaitOne(20);

                revLen = ReceiveData(revBuf, iRemain, 4000);
                if (revLen > 0)
                {
                    revLen += iRemain;
                    revLen = revLen > 3800 ? 3800 : revLen;
                    // 通道数据分发
                    int i = 0;

                    while (revLen - i >= 3 + frameDataLen)
                    {

                        if (revBuf[i] == 0xFF)
                        {
                            byte checkSum = GetCheckSum(revBuf, i, i + 2 + frameDataLen);

                            if (checkSum == revBuf[i + 2 + frameDataLen])
                            {

                                for (int j = 0; j < CHBuffers.Count; j++)
                                {
                                    if (CHBuffers[j].Count > 4096) continue;
                                    uint dataHigh = revBuf[2 + i + 2 * j];   // C#中UInt16无法进行移位操作
                                    uint dataLow = revBuf[2 + i + 2 * j + 1];
                                    CHBuffers[j].TryAdd((ushort)(dataHigh << 8 | dataLow));

                                }
                                i += 3 + frameDataLen;

                            }
                            else
                            {
                                i++; // 跳过无法解析的字节
                            }
                        }
                        else
                        {
                            i++; // 跳过无法解析的字节
                        }

                    }

                    // 可能还有剩余不到一帧的数据，填入到下次解析
                    iRemain = revLen - i;
                    for (int k = 0; k < iRemain; k++)
                    {
                        revBuf[k] = revBuf[i + k];
                    }
                }
                else
                {
                    revErrCount++;
                }

            } while (IsReceiving);   // revErrCount < 10 &&


        }

        public bool Write32BitVariable(float fValue, uint u32Addr, int dataType, int dataScale)
        {
            byte[] dataBuf = new byte[32];

            // Command-Address-DataTypeIndex-DataScale-Data-CheckSum
            int i = 0;
            dataBuf[i++] = 0xFF;
            dataBuf[i++] = 0x1F;

            dataBuf[i++] = (byte)(u32Addr >> 24 & 0xFF);
            dataBuf[i++] = (byte)(u32Addr >> 16 & 0xFF);
            dataBuf[i++] = (byte)(u32Addr >> 8 & 0xFF);
            dataBuf[i++] = (byte)(u32Addr & 0xFF);

            dataBuf[i++] = (byte)dataType;
            dataBuf[i++] = (byte)dataScale;

            uint data = 0;
            if (dataType == 0)
            {
                // int16
                fValue = fValue > short.MaxValue ? short.MaxValue : fValue;
                fValue = fValue < short.MinValue ? short.MinValue : fValue;
                data = (uint)(short)fValue;
            }
            else if (dataType == 1)
            {
                // float32
                for (int f = 0; f < dataScale; f++)
                {
                    fValue *= 10;
                }
                data = (uint)(int)fValue;
            }

            dataBuf[i++] = (byte)(data >> 24 & 0xFF);
            dataBuf[i++] = (byte)(data >> 16 & 0xFF);
            dataBuf[i++] = (byte)(data >> 8 & 0xFF);
            dataBuf[i++] = (byte)(data & 0xFF);

            dataBuf[i] = GetCheckSum(dataBuf, 0, i);

            return SendData(dataBuf, i + 1);
        }


        private static byte GetCheckSum(byte[] data, int begin, int end)
        {
            byte sum = 0;
            for (int i = begin; i < end; i++)
            {
                sum += data[i];
            }
            return sum;
        }

        private static int DataPack(byte data, byte[] dataBuffer, int idx)
        {
            int dataLen;
            if (data == 0xFF || data == 0xFE)
            {
                dataBuffer[idx] = 0xFE;
                dataBuffer[idx + 1] = data == 0xFF ? (byte)0x01 : (byte)0x00;
                dataLen = 2;
            }
            else
            {
                dataBuffer[idx] = data;
                dataLen = 1;
            }

            return dataLen;
        }




    }
}
