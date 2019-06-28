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

        public const int ERR_NO_ERROR =         0;
        public const int ERR_NO_CONNECTION =   -1;
        public const int ERR_TRUNCATED_RSP =   -2;    // message buffer is cut short
        public const int ERR_INCOMPLETE_RSP =  -3;    // fewer bytes received than expected

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
        private int portNum = MODBUS_TCP_PORT;

        private bool msgPending = false;
        private object portLock = new object();

        private byte[] txbuf = new byte[12];
        private byte[] rxbuf = new byte[16];

        private int errorCount = 0;
        private uint totalTXbytes = 0;
        private uint totalRXbytes = 0;

        #region Properties

        protected System.Net.IPAddress ctlAddr;
        public Int32 Address
        {
            get
            {
                // put the address in host order
                byte[] octets = ctlAddr.GetAddressBytes();
                Int32 addr = octets[0];
                addr = (addr << 8) | octets[1];
                addr = (addr << 8) | octets[2];
                addr = (addr << 8) | octets[3];

                return addr;
            }
            set
            {
                ctlAddr = new System.Net.IPAddress( System.Net.IPAddress.HostToNetworkOrder( value ) );
            }
        }

        public byte DeviceAddr { get; set; }

        public bool LoggingEnabled { get; set; }

        #endregion

        public Controller()
        {
        }

        #region startup and shutdown

        public bool Init()
        {
            Logger.Log( EventLevel.Info, "Connecting to MODBUS/TCP controller at " +
                                        ctlAddr.ToString() );
            try {
                sck = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );
                sck.NoDelay = true;
                sck.ReceiveTimeout = 100;
                sck.Connect( ctlAddr, portNum );
            }
            catch( Exception ex ) {
                // if it fails, just be graceful and report an error
                if( sck != null && sck.Connected ) {
                    sck.Close();
                    sck = null;
                }

                Logger.Log( EventLevel.Error, "Error connecting to MODBUS controller: "
                                                + ex.Message );
                return false;
            }

            if( !sck.Connected ) {
                Logger.Log( EventLevel.Error, "Unable to connect to MODBUS controller." );
                return false;
            }

            modbusMsgNum = 1;

            return true;
        }

        public bool Init( Int32 NetworkAddr, byte DevAddr )
        {
            Address = NetworkAddr;
            DeviceAddr = DevAddr;

            return Init();
        }

        public bool Init( Int32 NetworkAddr, byte DevAddr, int TcpPortNum )
        {
            Address = NetworkAddr;
            DeviceAddr = DevAddr;
            portNum = TcpPortNum;

            return Init();
        }

        public void Shutdown()
        {
            if( sck != null && sck.Connected ) {
                sck.Close();
                sck = null;
            }
        }

        #endregion

        /// <summary>
        /// Sends the command and waits for the response
        /// </summary>
        /// <param name="cmd">Command to send</param>
        /// <param name="rsp">Response received</param>
        /// <remarks>
        /// The first two bytes of command buffer are overwritten with the message number.
        /// </remarks>
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
                // log raw transmit data
                if( LoggingEnabled ) {
                    StringBuilder txStr = new StringBuilder();
                    txStr.AppendFormat( "msg:[{0:x02}{1:x02}] cnt:[{2:x02}{3:x02}] cmd:[{4:x02}{5:x02}] ",
                                        cmd[0], cmd[1], cmd[4], cmd[5], cmd[6], cmd[7] );
                    for( int idx = 8; idx < cmd.Length; idx++ ) {
                        txStr.AppendFormat( " {0:x02}", cmd[idx] );
                    }
                    Logger.Log( EventLevel.Debug, "tx> " + txStr.ToString() );
                }

                int retry;
                for( retry = 0; retry < 4; retry++ ) {
                    sck.Send( cmd );

                    totalTXbytes += (uint)cmd.Length;

                    Thread.Sleep( 25 );     // !!! better way to delay until data comes back?

                    byteCount = sck.Receive( rsp, rsp.Length, SocketFlags.None );

                    if( byteCount > 0 ) {
                        // wictory!
                        // log raw rx data
                        if( LoggingEnabled ) {
                            StringBuilder rxStr = new StringBuilder();
                            rxStr.AppendFormat( "msg:[{0:x02}{1:x02}] cnt:[{2:x02}{3:x02}] cmd:[{4:x02}{5:x02}] ",
                                                rsp[0], rsp[1], rsp[4], rsp[5], rsp[6], rsp[7] );
                            for( int idx = 8; idx < byteCount; idx++ ) {
                                rxStr.AppendFormat( " {0:x02}", rsp[idx] );
                            }
                            Logger.Log( EventLevel.Debug, "rx< " + rxStr.ToString() );
                        }

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
                    sck.Connect( ctlAddr, portNum );

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
        /// <returns>0 on success, negative on communication error, positive on MODBUS error</returns>
        public int WriteReg( UInt32 regAddr, int val )
        {
            if( sck == null || !sck.Connected ) {
                return ERR_NO_CONNECTION;
            }
            
            lock( portLock ) {
                //txbuf[0] and [1] hold a message ID
                txbuf[2] = 0;       // protocol ID
                txbuf[3] = 0;       //   MODBUS ID = 0

                txbuf[4] = 0;       // hi byte of data length
                txbuf[5] = 6;       // lo byte of data length

                txbuf[6] = DeviceAddr;
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

                    return ERR_TRUNCATED_RSP;
                }

                if( (rxbuf[7] & 0x80) != 0 ) {
                    Logger.Log( EventLevel.Warning, String.Format( "MODBUS error 0x{0:X} writing register {1}",
                                                                    rxbuf[8], regAddr ) );

                    return rxbuf[8];
                }
            }

            return ERR_NO_ERROR;
        }

        /// <summary>
        /// Reads a device input
        /// </summary>
        /// <param name="regAddr">Input address</param>
        /// <param name="val">16-bit value returned by device</param>
        /// <returns>0 on success, negative on communication error, positive on MODBUS error</returns>
        public int ReadInput( UInt32 regAddr, ref int result )
        {
            if( sck == null || !sck.Connected ) {
                return ERR_NO_CONNECTION;
            }

            lock( portLock ) {
                //txbuf[0] and [1] hold a message ID
                txbuf[2] = 0;       // protocol ID
                txbuf[3] = 0;       //   MODBUS ID = 0

                txbuf[4] = 0;       // hi byte of data length
                txbuf[5] = 6;       // lo byte of data length

                txbuf[6] = DeviceAddr;
                txbuf[7] = CMD_READINPUT;

                txbuf[8] = (byte)((regAddr >> 8) & 0xff);   // addr hi byte
                txbuf[9] = (byte)(regAddr & 0xff);          // addr lo byte
                txbuf[10] = 0;
                txbuf[11] = 1;                               // read a single 16-bit register

                int rxCount = SendRcvCmd( txbuf, ref rxbuf );
                if( rxCount < 8 ) {
                    // minimum message length is 8 bytes
                    Logger.Log( EventLevel.Warning, String.Format( "Truncated MODBUS response to READREG. reg addr = {0}",
                                        regAddr ) );

                    StringBuilder rxStr = new StringBuilder();
                    for( int idx = 0; idx < rxCount; idx++ ) {
                        rxStr.AppendFormat( "{0:x02} ", rxbuf[idx] );
                    }

                    Logger.Log( EventLevel.Debug, rxStr.ToString() );

                    return ERR_TRUNCATED_RSP;
                }

                if( (rxbuf[7] & 0x80) != 0 ) {
                    Logger.Log( EventLevel.Warning, String.Format( "MODBUS error 0x{0:X} reading register {1}",
                                                                    rxbuf[8], regAddr ) );
                    return rxbuf[8];
                }

                if( rxCount < 10 ) {
                    // minimum good response is 7-byte header + command code + returned value
                    Logger.Log( EventLevel.Warning, String.Format( "Short MODBUS response to READINPUT. reg addr = {0}",
                                        regAddr ) );

                    StringBuilder rxStr = new StringBuilder();
                    rxStr.AppendFormat( "msg:[{0:x02}{1:x02}] cnt:[{2:x02}{3:x02}] cmd:[{4:x02}{5:x02}] ",
                                        rxbuf[0], rxbuf[1], rxbuf[4], rxbuf[5], rxbuf[6], rxbuf[7] );
                    for( int idx = 8; idx < rxCount; idx++ ) {
                        rxStr.AppendFormat( " {0:x02}", rxbuf[idx] );
                    }

                    Logger.Log( EventLevel.Debug, rxStr.ToString() );

                    return ERR_INCOMPLETE_RSP;
                }

                result = rxbuf[8];
                result = (result << 8) + rxbuf[9];
            }

            return ERR_NO_ERROR;
        }

        /// <summary>
        /// Reads a MODBUS register
        /// </summary>
        /// <param name="regAddr">Register address</param>
        /// <param name="val">16-bit value returned by device</param>
        /// <returns>0 on success, negative on communication error, positive on MODBUS error</returns>
        public int ReadReg( UInt32 regAddr, ref int result )
        {
            if(sck == null || !sck.Connected ) {
                return ERR_NO_CONNECTION;
            }

            lock( portLock ) {
                //txbuf[0] and [1] hold a message ID
                txbuf[2] = 0;       // protocol ID
                txbuf[3] = 0;       //   MODBUS ID = 0

                txbuf[4] = 0;       // hi byte of data length
                txbuf[5] = 6;       // lo byte of data length

                txbuf[6] = DeviceAddr;
                txbuf[7] = CMD_READREG;

                txbuf[8] = (byte)((regAddr >> 8) & 0xff);   // addr hi byte
                txbuf[9] = (byte)(regAddr & 0xff);          // addr lo byte
                txbuf[10] = 0;
                txbuf[11] = 1;                               // read a single 16-bit register

                int rxCount = SendRcvCmd( txbuf, ref rxbuf );
                if( rxCount < 8 ) {
                    // minimum message length is 8 bytes
                    Logger.Log( EventLevel.Warning, String.Format( "Truncated MODBUS response to READREG. reg addr = {0}",
                                        regAddr ) );

                    StringBuilder rxStr = new StringBuilder();
                    for( int idx = 0; idx < rxCount; idx++ ) {
                        rxStr.AppendFormat( "{0:x02} ", rxbuf[idx] );
                    }

                    Logger.Log( EventLevel.Debug, rxStr.ToString() );

                    return ERR_TRUNCATED_RSP;
                }

                if( (rxbuf[7] & 0x80) != 0 ) {
                    Logger.Log( EventLevel.Warning, String.Format( "MODBUS error 0x{0:X} reading register {1}",
                                                                    rxbuf[8], regAddr ) );
                    return rxbuf[8];
                }

                if( rxCount < 11 ) {
                    // minimum good response is 7-byte header + command code + returned value
                    Logger.Log( EventLevel.Warning, String.Format( "Short MODBUS response to READREG. reg addr = {0}",
                                        regAddr ) );

                    StringBuilder rxStr = new StringBuilder();
                    rxStr.AppendFormat( "msg:[{0:x02}{1:x02}] cnt:[{2:x02}{3:x02}] cmd:[{4:x02}{5:x02}] ",
                                        rxbuf[0], rxbuf[1], rxbuf[4], rxbuf[5], rxbuf[6], rxbuf[7] );
                    for( int idx = 8; idx < rxCount; idx++ ) {
                        rxStr.AppendFormat( " {0:x02}", rxbuf[idx] );
                    }

                    Logger.Log( EventLevel.Debug, rxStr.ToString() );

                    return ERR_INCOMPLETE_RSP;
                }

                result = rxbuf[9];
                result = (result << 8) + rxbuf[10];
            }

            return ERR_NO_ERROR;
        }

        /// <summary>
        /// Writes a MODBUS flag register.
        /// </summary>
        /// <param name="regAddr">Register address</param>
        /// <param name="val">Value to write, 0 or 0xFF</param>
        /// <returns>0 on success, negative on communication error, positive on MODBUS error</returns>
        public int SetFlagReg( UInt32 regAddr, int val )
        {
            if( sck == null || !sck.Connected ) {
                return ERR_NO_CONNECTION;
            }

            lock( portLock ) {
                //txbuf[0] and [1] hold a message ID
                txbuf[2] = 0;       // protocol ID
                txbuf[3] = 0;       //   MODBUS ID = 0

                txbuf[4] = 0;       // hi byte of data length
                txbuf[5] = 6;       // lo byte of data length

                txbuf[6] = DeviceAddr;
                txbuf[7] = CMD_WRITEBIT;

                txbuf[8] = (byte)((regAddr >> 8) & 0xff);   // addr hi byte
                txbuf[9] = (byte)(regAddr & 0xff);          // addr lo byte
                txbuf[10] = (byte)(val & 0xff);
                txbuf[11] = 0;

                int rxCount = SendRcvCmd( txbuf, ref rxbuf );
                if( rxCount < 8 ) {
                    // minimum response is 7-byte header + command code
                    Logger.Log( EventLevel.Warning, String.Format( "Short MODBUS response to WRITEBIT. reg addr = {0}, {1} bytes rcvd.",
                                        regAddr, rxCount ) );
                    StringBuilder rxStr = new StringBuilder();
                    for( int idx = 0; idx < rxCount; idx++ ) {
                        rxStr.AppendFormat( "{0:x02} ", rxbuf[idx] );
                    }

                    Logger.Log( EventLevel.Debug, rxStr.ToString() );

                    return ERR_TRUNCATED_RSP;
                }

                if( (rxbuf[7] & 0x80) != 0 ) {
                    Logger.Log( EventLevel.Warning, String.Format( "MODBUS error 0x{0:X} setting flag {1}",
                                                                    rxbuf[8], regAddr ) );
                    return rxbuf[8];
                }
            }

            return ERR_NO_ERROR;
        }

        /// <summary>
        /// Reads a MODBUS register
        /// </summary>
        /// <param name="regAddr">Register address</param>
        /// <param name="val">0 or 0xFF value returned by device</param>
        /// <returns>0 on success, negative on communication error, positive on MODBUS error</returns>
        public int GetFlagReg( UInt32 regAddr, ref int result )
        {
            if( sck == null || !sck.Connected ) {
                return ERR_NO_CONNECTION;
            }

            //txbuf[0] and [1] hold a message ID
            txbuf[2] = 0;       // protocol ID
            txbuf[3] = 0;       //   MODBUS ID = 0

            txbuf[4] = 0;       // hi byte of data length
            txbuf[5] = 6;       // lo byte of data length

            txbuf[6] = DeviceAddr;
            txbuf[7] = CMD_READBIT;

            txbuf[8] = (byte)((regAddr >> 8) & 0xff);   // addr hi byte
            txbuf[9] = (byte)(regAddr & 0xff);          // addr lo byte
            txbuf[10] = 0;
            txbuf[11] = 1;                               // read a single register

            int rxCount = SendRcvCmd( txbuf, ref rxbuf );
            if( rxCount < 8 ) {
                // minimum response is 7-byte header + command code
                Logger.Log( EventLevel.Warning, String.Format( "Truncated MODBUS response to WRITEBIT. reg addr = {0}, {1} bytes rcvd.",
                                    regAddr, rxCount ) );
                StringBuilder rxStr = new StringBuilder();
                for( int idx = 0; idx < rxCount; idx++ ) {
                    rxStr.AppendFormat( "{0:x02} ", rxbuf[idx] );
                }

                Logger.Log( EventLevel.Debug, rxStr.ToString() );

                return ERR_TRUNCATED_RSP;
            }

            if( (rxbuf[7] & 0x80) != 0 ) {
                Logger.Log( EventLevel.Warning, String.Format( "MODBUS error 0x{0:X} reading register {1}",
                                                                rxbuf[8], regAddr ) );
                return rxbuf[8];
            }

            if( rxCount < 10 ) {
                // minimum response is 7-byte header + command code + returned value
                Logger.Log( EventLevel.Warning, String.Format( "Short MODBUS response to READREG. reg addr = {0}",
                                    regAddr ) );

                StringBuilder rxStr = new StringBuilder();
                rxStr.AppendFormat( "msg:[{0:x02}{1:x02}] cnt:[{2:x02}{3:x02}] cmd:[{4:x02}{5:x02}] ",
                                    rxbuf[0], rxbuf[1], rxbuf[4], rxbuf[5], rxbuf[6], rxbuf[7] );
                for( int idx = 8; idx < rxCount; idx++ ) {
                    rxStr.AppendFormat( " {0:x02}", rxbuf[idx] );
                }

                Logger.Log( EventLevel.Debug, rxStr.ToString() );
                return ERR_INCOMPLETE_RSP;
            }

            // single byte response
            result = rxbuf[9];

            return ERR_NO_ERROR;
        }
    }
}