using System.IO.Ports;

using Mofucat.SerialIO;

using var serialPort = new SerialPort("COM9");
using var reader = new SerialPortReader(serialPort, delimiter: [(byte)'\r'], maxBufferSize: 16, ownsSerialPort: true);

reader.BufferOverflow += (_, size) =>
{
    Console.WriteLine($"Warning overflow. size=[{size}])");
};
reader.LineReceived += (_, bytes) =>
{
    Console.WriteLine($"Received. bytes=[{Convert.ToHexString(bytes)}]");
};

serialPort.Open();

Console.ReadLine();

serialPort.Close();
