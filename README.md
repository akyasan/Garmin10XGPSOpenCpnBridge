# Garmin GPS 10x NMEA TCP Bridge

This Windows app reads NMEA 0183 sentences from the Garmin Bluetooth serial
port and publishes them over a TCP server bound to `127.0.0.1`. It does not use
com0com or install a virtual serial driver. The listener is restricted to this
PC and is not exposed to the local network.

## Use with OpenCPN

1. Power and pair the GPS in Windows Bluetooth settings.
2. Run `bin\GarminGpsBridge.exe`, select the Bluetooth GPS serial port, and
   click **Connect GPS**. Opening the serial port initiates the Bluetooth SPP
   connection. A successful port open alone does not confirm that data is
   arriving.
3. The default GPS baud rate is `4800`, the standard NMEA serial rate. Change
   it if the receiver is configured differently.
4. Click **Start TCP stream**. The server listens on `127.0.0.1:10110` by
   default. The port can be changed before starting the stream.
5. In OpenCPN, add a **Network** connection configured as **TCP**, **Input**,
   address `127.0.0.1`, and the same port (default `10110`). Start the
   connection and confirm the bridge shows an OpenCPN TCP client.
6. Watch the NMEA monitor, byte count, and GPS-fix status. Continuous NMEA
   sentences can report no valid position; GGA quality `0` or RMC status `V`
   means the receiver has no fix.
7. Click **Stop stream** to stop sending data while leaving Bluetooth
   connected. Click **Disconnect GPS** to close the serial connection.

If the receiver needs its NMEA output enabled, click **Request NMEA mode**
while connected. This sends the GPS 10x mode-switch command at 9600 baud and
restores the selected baud afterward. The app reports whether it observed an
NMEA response, but cannot guarantee a mode change if the receiver is not
connected or does not reply. Power cycling may be needed for reconnection.

Do not open the Bluetooth COM port in another program while the bridge is
connected. The GPS Bluetooth link and OpenCPN network connection have not been
tested on the physical receiver as part of the offline self-test.

## Build and offline test

The app targets .NET Framework 4.8 and can be rebuilt with the Windows .NET
Framework C# compiler:

```powershell
.\build.ps1
```

Run the self-test without touching the Bluetooth GPS:

```powershell
.\bin\GarminGpsBridge.exe --self-test
```

The self-test checks NMEA no-fix and valid-fix parsing, then verifies that a
complete NMEA sentence is delivered unchanged over a loopback TCP connection.
