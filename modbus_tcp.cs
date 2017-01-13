/* Basic implementation of a ModbusTCP interface in C#. This is known to work with .Net 3.5. Will probably work with
 * other versions just fine.
 * Calls to Logger.Log reference a simple log file writer; implement your own error reporting there.
 * The MODBUS_TCP_PORT definition will vary according to your system.
 */

using System;
using System.Threading;
using System.Net.Sockets;

namespace ModbusTCP {
    public class Controller
    {
        #region Constants

        private const int MAX_ERRORS = 5;           // pulled out of a hat

        // MODBUS command codes
        private const int CMD_READBIT =    0x01;
        private const int CMD_READREG =    0x03;
        private const int CMD_WRITEBIT =   0x05;
        private const int CMD_WRITEREG =   0x06;
        private const int CMD_WRMULTIREG = 0x10;

        // other MODBUS settings
        private const UInt16 POLY16 =       0xA001;
        private const Int32 MODBUS_TCP_PORT = 502;

        #endregion

        private int modbusMsgNum;
        private Socket sck;
        private System.Net.IPEndPoint ctlAddr;

        private bool msgPending = false;

        private int errorCount = 0;
        private uint totalTXbytes = 0;
        private uint totalRXbytes = 0;

        public Controller()
        {
        }

        public void Init( int RemoteAddr )
        {
            int retry = 1;
            do {
                // try to connect to the controller
                try {
                    // for some reason we need to reverse the address,
                    //   e.g. "192.168.100.10" becomes 0x0a64a8c0
                    Int64 addr = (RemoteAddr & 0xFF);
                    addr = (addr << 8) | ((RemoteAddr >> 8) & 0xFF);
                    addr = (addr << 8) | ((RemoteAddr >> 16) & 0xFF);
                    addr = (addr << 8) | ((RemoteAddr >> 24) & 0xFF);
                    ctlAddr = new System.Net.IPEndPoint( addr & 0xFFFFFFFF, MODBUS_TCP_PORT );

                    sck = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );
                    sck.NoDelay = true;
                    sck.ReceiveTimeout = 100;
                    sck.Connect( ctlAddr );

                    installed = true;
                    break;
                }
                catch( Exception ex ) {
                    // if it fails, just be graceful and report an error
                    if( sck != null && sck.Connected ) {
                        sck.Close();
                        sck = null;
                    }

                    Logger.Log( EventLevel.Error, "Error connecting to ModbusTCP controller: "
                                                    + ex.Message );
                }

                Thread.Sleep( 1000 );
            } while( retry++ <= 3 );

            modbusMsgNum = 1;
        }

        /// <summary>
        /// Sends the command and waits for the response
        /// </summary>
        /// <param name="cmd">Command to send</param>
        /// <param name="rsp">Response received</param>
        private int SendRcvCmd( byte[] cmd, ref byte[] rsp )
        {
            if( cmd == null ||cmd.Length == 0 ) {
                Logger.Log( EventLevel.Error, "Null command passed to Controller.SendRcvCmd" );
                return 0;
            }

            if( rsp == null || rsp.Length == 0 ) {
                Logger.Log( EventLevel.Error, "Null receive buffer passed to Controller.SendRcvCmd" );
                return 0;
            }

            if( sck == null || !sck.Connected ){
                try {
                    sck = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );
                    sck.NoDelay = true;
                    sck.ReceiveTimeout = 250;
                    sck.Connect( ctlAddr );
                }
                catch( Exception ex ) {
                    errorCount++;
                    Logger.Log( EventLevel.Warning,
                                "Error reconnecting with controller: " + ex.Message ) );
                    return 0;
                }
            }

            int timeout = 500;
            while( msgPending ) {
                Thread.Sleep( 25 );
                timeout -= 25;
                if( timeout < 0 ) {
                    errorCount++;
                    Logger.Log( EventLevel.Warning, "Timeout waiting for command to complete" );
                    return 0;
                }
            }

            msgPending = true;

            cmd[0] = (byte)((modbusMsgNum >> 8) & 0xFF);
            cmd[1] = (byte)(modbusMsgNum & 0xff);
            modbusMsgNum++;

            bool reconnectRequired = false;
            int byteCount = 0;
            try {
/*
                // log raw transmit data
                StringBuilder txStr = new StringBuilder();
                for( int idx = 0; idx < cmd.Length; idx++ ) {
                    txStr.AppendFormat( "{0:x02} ", cmd[idx] );
                }
                Logger.Log( EventLevel.Debug, "tx> " + txStr.ToString() );
*/

                int retry;
                for( retry = 0; retry < 4; retry++ ) {
                    sck.Send( cmd );

                    totalTXbytes += (uint)cmd.Length;

                    Thread.Sleep( 25 );     // !!! better way to delay until data comes back?

                    byteCount = sck.Receive( rsp, rsp.Length, SocketFlags.None );

                    if( byteCount > 0 ) {
                        // wictory!
/*
                        // log raw rx data
                        StringBuilder rxStr = new StringBuilder();
                        for( int idx = 0; idx < byteCount; idx++ ) {
                            rxStr.AppendFormat( "{0:x02} ", rsp[idx] );
                        }
                        Logger.Log( EventLevel.Debug, "rx< " + rxStr.ToString() );
*/

                        totalRXbytes += (uint)byteCount;
                        break;
                    } else {
                        // else, try again
                        if( !sck.Connected || retry >= 3 ) {
                            errorCount++;

                            Logger.Log( EventLevel.Warning,
                                "Communication with controller lost, resynchronizing" ) );

                            sck.Close();
                            reconnectRequired = true;
                            break;
                        }
                    }
                }
            }
            catch( Exception ex ) {
                // something got borked bad enough to lose our connection
                msgPending = false;
                byteCount = 0;
                Logger.Log( EventLevel.Warning, ex.Message + " in Controller.SendRecvCmd at "
                                                + ex.StackTrace );

                // flag ourselves as needing help
                sck.Close();
                reconnectRequired = true;

                errorCount++;
            }

            if( reconnectRequired ) {
                try {
                    // reconnect
                    sck = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );
                    sck.NoDelay = true;
                    sck.ReceiveTimeout = 250;
                    sck.Connect( ctlAddr );

                    // and try once more
                    sck.Send( cmd );

                    totalTXbytes += (uint)cmd.Length;

                    Thread.Sleep( 25 );
                    byteCount = sck.Receive( rsp, rsp.Length, SocketFlags.None );

                    totalRXbytes += (uint)byteCount;
                }
                catch( Exception ex ) {
                    Logger.Log( EventLevel.Error, "Lost communicatoin with controller: " + ex.Message );
                }
            }

            msgPending = false;

            return byteCount;
        }

        /// <summary>
        /// Formats a register write command
        /// </summary>
        /// <param name="regAddr">MODBUS register address</param>
        /// <param name="val">16-bit value to write</param>
        public bool WriteReg( int regAddr, int val )
        {
            if( disabled || sck == null || !sck.Connected ) {
                return false;
            }

            bool successFlag = true;

            byte[] txbuf = new byte[12];
            byte[] rxbuf = new byte[16];        // !!! check this

            //txbuf[0] and [1] hold a message ID
            txbuf[2] = 0;       // protocol ID
            txbuf[3] = 0;       //   MODBUS ID = 0

            txbuf[4] = 0;       // hi byte of data length
            txbuf[5] = 6;       // lo byte of data length

            txbuf[6] = modbusAddr;
            txbuf[7] = CMD_WRITEREG;

            txbuf[8] = (byte)((regAddr >> 8) & 0xff);   // addr hi byte
            txbuf[9] = (byte)(regAddr & 0xff);          // addr lo byte
            txbuf[10] = (byte)((val >> 8) & 0xff);
            txbuf[11] = (byte)(val & 0xff);

            int rcvdbytes = SendRcvCmd( txbuf, ref rxbuf );
            if( rcvdbytes < 8 ) {
                // minimum response is 7-byte header + command code
                Logger.Log( EventLevel.Warning, String.Format( "Short MODBUS response to WRITEREG. reg addr = {0}",
                                        regAddr ) );

                successFlag = false;
            }

            if( (rxbuf[7] & 0x80) != 0 ) {
                Logger.Log( EventLevel.Warning, String.Format( "MODBUS error 0x{0:X} writing register {1}",
                                                                    rxbuf[8], regAddr ) );
                successFlag = false;
            }

            return successFlag;
        }

        public bool ReadReg( int regAddr, ref int result )
        {
            if( disabled || sck == null || !sck.Connected ) {
                return false;
            }

            bool successFlag = true;

            byte[] txbuf = new byte[12];
            byte[] rxbuf = new byte[16];

            //txbuf[0] and [1] hold a message ID
            txbuf[2] = 0;       // protocol ID
            txbuf[3] = 0;       //   MODBUS ID = 0

            txbuf[4] = 0;       // hi byte of data length
            txbuf[5] = 6;       // lo byte of data length

            txbuf[6] = modbusAddr;
            txbuf[7] = CMD_READREG;

            txbuf[8] = (byte)((regAddr >> 8) & 0xff);   // addr hi byte
            txbuf[9] = (byte)(regAddr & 0xff);          // addr lo byte
            txbuf[10] = 0;
            txbuf[11] = 1;                              // read a single 16-bit register

            int rxCount = SendRcvCmd( txbuf, ref rxbuf );
            if( rxCount < 8 ) {
                // minimum response is 7-byte header + command code
                Logger.Log( EventLevel.Warning, String.Format( "Short MODBUS response to READREG. reg addr = {0}",
                                        regAddr ) );

                successFlag = false;
            }

            if( (rxbuf[7] & 0x80) != 0 ) {
                Logger.Log( EventLevel.Warning, String.Format( "MODBUS error 0x{0:X} reading register {1}",
                                                                    rxbuf[8], regAddr ) );
                successFlag = false;
            }

            if( rxCount >= 11 ) {
                result = rxbuf[9];
                result = (result << 8) + rxbuf[10];
            } else {
                // if we don't get a value back, then it's a failure
                result = 0;
                successFlag = false;
            }

            return successFlag;
        }

        public bool SetFlagReg( int regAddr, int val )
        {
            if( disabled || sck == null || !sck.Connected ) {
                return false;
            }

            bool successFlag = true;

            byte[] txbuf = new byte[12];
            byte[] rxbuf = new byte[16];

            //txbuf[0] and [1] hold a message ID
            txbuf[2] = 0;       // protocol ID
            txbuf[3] = 0;       //   MODBUS ID = 0

            txbuf[4] = 0;       // hi byte of data length
            txbuf[5] = 6;       // lo byte of data length

            txbuf[6] = modbusAddr;
            txbuf[7] = CMD_WRITEBIT;

            txbuf[8] = (byte)((regAddr >> 8) & 0xff);   // addr hi byte
            txbuf[9] = (byte)(regAddr & 0xff);          // addr lo byte
            txbuf[10] = (byte)(val & 0xff);
            txbuf[11] = 0;

            int rxcnt = SendRcvCmd( txbuf, ref rxbuf );
            if( rxcnt < 8 ) {
                // minimum response is 7-byte header + command code
                Logger.Log( EventLevel.Warning, String.Format( "Short MODBUS response to WRITEBIT. reg addr = {0}, {1} bytes rcvd.",
                                        regAddr, rxcnt ) );

                successFlag = false;
            }

            if( (rxbuf[7] & 0x80) != 0 ) {
                Logger.Log( EventLevel.Warning, String.Format( "MODBUS error 0x{0:X} setting flag {1}",
                                                                    rxbuf[8], regAddr ) );

                successFlag = false;
            }

            return successFlag;
        }

        public bool GetFlagReg( int regAddr, ref int result )
        {
            if( disabled || sck == null || !sck.Connected ) {
                return false;
            }

            bool successFlag = true;
            byte[] txbuf = new byte[12];
            byte[] rxbuf = new byte[16];

            //txbuf[0] and [1] hold a message ID
            txbuf[2] = 0;       // protocol ID
            txbuf[3] = 0;       //   MODBUS ID = 0

            txbuf[4] = 0;       // hi byte of data length
            txbuf[5] = 6;       // lo byte of data length

            txbuf[6] = modbusAddr;
            txbuf[7] = CMD_READBIT;

            txbuf[8] = (byte)((regAddr >> 8) & 0xff);   // addr hi byte
            txbuf[9] = (byte)(regAddr & 0xff);          // addr lo byte
            txbuf[10] = 0;
            txbuf[11] = 1;                               // read a single 16-bit register

            int rxCount = SendRcvCmd( txbuf, ref rxbuf );
            if( rxCount < 8 ) {
                // minimum response is 7-byte header + command code
                Logger.Log( EventLevel.Warning, String.Format( "Short MODBUS response to READREG. reg addr = {0}",
                                    regAddr ) );
                successFlag = false;
            }

            if( (rxbuf[7] & 0x80) != 0 ) {
                Logger.Log( EventLevel.Warning, String.Format( "MODBUS error 0x{0:X} reading register {1}",
                                                                rxbuf[8], regAddr ) );
                successFlag = false;
            }

            if( rxCount >= 10 ) {
                // single byte response
                result = rxbuf[9];
            } else {
                // if we don't get a value back, then it's a failure
                result = 0;
                successFlag = false;
            }

            return successFlag;
        }

        public void Shutdown()
        {
            Logger.Log( EventLevel.Info, "ModbusTCP.Shutdown" );

            try {
                sck.Close();
            }
            catch {}
            finally {
                sck = null;
            }
        }
    }
}