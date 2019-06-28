using System;
using System.Text;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using EagleHW;

namespace Tests
{
    /// <summary>
    /// Test class for MODBUS/TCP client
    /// </summary>
    [TestClass()]
    public class ModbusTests
    {
        private TestContext testContextInstance;

        /// <summary>
        ///Gets or sets the test context which provides
        ///information about and functionality for the current test run.
        ///</summary>
        public TestContext TestContext
        {
            get
            {
                return testContextInstance;
            }
            set
            {
                testContextInstance = value;
            }
        }

        public ModbusTCP TestClient;
        public ModbusTestEndpoint TestServer;

        #region Additional test attributes
        //
        // You can use the following additional attributes as you write your tests:
        //
        // Use ClassInitialize to run code before running the first test in the class
        // [ClassInitialize()]
        // public static void MyClassInitialize(TestContext testContext) { }
        //
        // Use ClassCleanup to run code after all tests in a class have run
        // [ClassCleanup()]
        // public static void MyClassCleanup() { }
        //
        // Use TestInitialize to run code before running each test 
        // [TestInitialize()]
        // public void MyTestInitialize() { }
        //
        // Use TestCleanup to run code after each test has run
        // [TestCleanup()]
        // public void MyTestCleanup() { }
        //
        #endregion

        [TestInitialize()]
        public void EstablishClientServer()
        {
            TestClient = new ModbusTCP();
            Assert.IsNotNull( TestClient );

            TestServer = new ModbusTestEndpoint();
            Assert.IsNotNull( TestServer );

            TestServer.Open( 5000 );    // must be > 1024

            // connect to loopback address 127.0.0.1
            bool clientReady = TestClient.Init( 0x7F000001, 0, 5000 );
            Assert.AreEqual( clientReady, true );
        }

        [TestCleanup()]
        public void CleanupClientServer()
        {
            TestClient.Shutdown();
            TestServer.Close();
        }

        /// <summary>
        /// Simple echo test to make sure data can flow round trip.
        /// </summary>
        [TestMethod()]
        public void EchoTest()
        {
            byte[] testData = new byte[] { 0x10, 0, 0x5A, 0xA5, 0, 0xFF };
            byte[] rxData = new byte[testData.Length];

            TestServer.SetNextResponse( testData );
            int byteCount = TestClient.SendMessage( testData, ref rxData );

            // make sure we got the right number of bytes returned
            Assert.AreEqual( byteCount, testData.Length );

            // make sure they are echoed correctly
            for( int idx = 0; idx < testData.Length; idx++ ) {
                Assert.AreEqual( testData[idx], rxData[idx] );
            }
        }

#if false
        [TestMethod()]
        public void ErrorsTest()
        {
            // add test for checking the message number
            // add test for checking the data length
            // add test for checking the device ID ?
            // add test for checking the command code
        }
#endif

        /// <summary>
        /// A test for writing a MODBUS register
        /// </summary>
        [TestMethod()]
        public void RegisterWriteTest()
        {
            // client and server are set up already
            TestClient.DeviceAddr = 22;     // just to stand out

            // start with a truncated message
            byte[] responseToSend = new byte[] { 0, 1, 0, 0, 0, 6 };
            TestServer.SetNextResponse( responseToSend );

            int status = TestClient.WriteReg( 5, 100 );
            Assert.AreEqual( status, ModbusTCP.ERR_TRUNCATED_RSP );

            // this should echo the command, but nothing else
            // note: first two bytes are message number. We don't care right now, but might at some point
            responseToSend = new byte[] { 0, 2, 0, 0, 0, 6, TestClient.DeviceAddr, ModbusTCP.CMD_WRITEREG, 0 };
            TestServer.SetNextResponse( responseToSend );

            status = TestClient.WriteReg( 5, 100 );
            Assert.AreEqual( status, ModbusTCP.ERR_NO_ERROR );

            // now test the message's error flag
            byte errCode = 15;      // any non-zero number

            responseToSend[8] = errCode;
            responseToSend[7] |= 0x80;  // set error flag
            TestServer.SetNextResponse( responseToSend );

            status = TestClient.WriteReg( 5, 100 );

            // error code should be returned in the status
            Assert.AreEqual( status, errCode );
        }

        /// <summary>
        /// A test for reading a MODBUS (holding) register
        /// </summary>
        [TestMethod()]
        public void RegisterReadTest()
        {
            // client and server are set up already
            TestClient.DeviceAddr = 25;     // just to stand out

            // start with a truncated message
            byte[] responseToSend = new byte[] { 0, 1, 0, 0, 0, 6 };
            TestServer.SetNextResponse( responseToSend );

            int expectedVal = 0x1020;
            int actualVal = 0;
            int status = TestClient.ReadReg( 5, ref actualVal );
            Assert.AreEqual( status, ModbusTCP.ERR_TRUNCATED_RSP );

            // this should echo the command and return a byte count plus value
            // note: first two bytes are message number. We don't care right now, but might at some point
            responseToSend = new byte[] { 0, 2, 0, 0, 0, 6,
                                            TestClient.DeviceAddr, ModbusTCP.CMD_READREG,
                                            2,      // byte count
                                           (byte)(expectedVal >> 8),
                                           (byte)(expectedVal & 0xFF) }; // 16 bit value
            TestServer.SetNextResponse( responseToSend );

            status = TestClient.ReadReg( 5, ref actualVal );
            Assert.AreEqual( status, ModbusTCP.ERR_NO_ERROR );
            Assert.AreEqual( expectedVal, actualVal );

            // now test the message's error flag
            byte errCode = 15;      // any non-zero number

            responseToSend[8] = errCode;
            responseToSend[7] |= 0x80;  // set error flag
            TestServer.SetNextResponse( responseToSend );

            status = TestClient.ReadReg( 5, ref actualVal );

            // error code should be returned in the status
            Assert.AreEqual( status, errCode );
        }

        /// <summary>
        /// A test for writing a MODBUS flag register
        /// </summary>
        [TestMethod()]
        public void WriteFlagTest()
        {
            // client and server are set up already
            TestClient.DeviceAddr = 18;     // just to stand out

            // start with a truncated message
            byte[] responseToSend = new byte[] { 0, 1, 0, 0, 0, 6 };
            TestServer.SetNextResponse( responseToSend );

            int status = TestClient.SetFlagReg( 5, 0xFF );
            Assert.AreEqual( status, ModbusTCP.ERR_TRUNCATED_RSP );

            // this should echo the command, but nothing else
            // note: first two bytes are message number. We don't care right now, but might at some point
            responseToSend = new byte[] { 0, 2, 0, 0, 0, 6, TestClient.DeviceAddr, ModbusTCP.CMD_WRITEBIT, 0 };
            TestServer.SetNextResponse( responseToSend );

            status = TestClient.SetFlagReg( 5, 0xFF );
            Assert.AreEqual( status, ModbusTCP.ERR_NO_ERROR );

            // now test the message's error flag
            byte errCode = 15;      // any non-zero number

            responseToSend[8] = errCode;     // set some "error" code
            responseToSend[7] |= 0x80;  // set error flag
            TestServer.SetNextResponse( responseToSend );

            status = TestClient.SetFlagReg( 5, 0xFF );

            // error code should be returned in the status
            Assert.AreEqual( status, errCode );
        }

        /// <summary>
        /// A test for reading a MODBUS (holding) register
        /// </summary>
        [TestMethod()]
        public void ReadFlagTest()
        {
            // client and server are set up already
            TestClient.DeviceAddr = 25;     // just to stand out

            // start with a truncated message
            byte[] responseToSend = new byte[] { 0, 1, 0, 0, 0, 6 };
            TestServer.SetNextResponse( responseToSend );

            byte expectedVal = 0xFF;
            int actualVal = 0;
            int status = TestClient.GetFlagReg( 5, ref actualVal );
            Assert.AreEqual( status, ModbusTCP.ERR_TRUNCATED_RSP );

            // this should echo the command and return a byte count plus value
            // note: first two bytes are message number. We don't care right now, but might at some point
            responseToSend = new byte[] { 0, 2, 0, 0, 0, 6,
                                            TestClient.DeviceAddr, ModbusTCP.CMD_READBIT,
                                            1,              // byte count
                                            expectedVal };  // register value
            TestServer.SetNextResponse( responseToSend );

            status = TestClient.GetFlagReg( 5, ref actualVal );
            Assert.AreEqual( status, ModbusTCP.ERR_NO_ERROR );
            Assert.AreEqual( expectedVal, actualVal );

            // now test the message's error flag
            byte errCode = 15;      // any non-zero number

            responseToSend[8] = errCode;
            responseToSend[7] |= 0x80;  // set error flag
            TestServer.SetNextResponse( responseToSend );

            status = TestClient.GetFlagReg( 5, ref actualVal );

            // error code should be returned in the status
            Assert.AreEqual( status, errCode );
        }

        /// <summary>
        /// A test for reading a MODBUS (holding) register
        /// </summary>
        [TestMethod()]
        public void RegisterInputTest()
        {
            // client and server are set up already
            TestClient.DeviceAddr = 25;     // just to stand out

            // start with a truncated message
            byte[] responseToSend = new byte[] { 0, 1, 0, 0, 0, 6 };
            TestServer.SetNextResponse( responseToSend );

            byte expectedVal = 0x01;
            int actualVal = 0;
            int status = TestClient.ReadInput( 5, ref actualVal );
            Assert.AreEqual( status, ModbusTCP.ERR_TRUNCATED_RSP );

            // this should echo the command and return a byte count plus value
            // note: first two bytes are message number. We don't care right now, but might at some point
            responseToSend = new byte[] { 0, 2, 0, 0, 0, 6,
                                            TestClient.DeviceAddr, ModbusTCP.CMD_READBIT,
                                            0, expectedVal };  // register value
            TestServer.SetNextResponse( responseToSend );

            status = TestClient.ReadInput( 5, ref actualVal );
            Assert.AreEqual( status, ModbusTCP.ERR_NO_ERROR );
            Assert.AreEqual( expectedVal, actualVal );

            // now test the message's error flag
            byte errCode = 15;      // any non-zero number

            responseToSend[8] = errCode;
            responseToSend[7] |= 0x80;  // set error flag
            TestServer.SetNextResponse( responseToSend );

            status = TestClient.ReadInput( 5, ref actualVal );

            // error code should be returned in the status
            Assert.AreEqual( status, errCode );
        }
    }
}
