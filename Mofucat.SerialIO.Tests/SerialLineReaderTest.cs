// ReSharper disable AccessToDisposedClosure
// ReSharper disable StringLiteralTypo
// ReSharper disable CommentTypo
namespace Mofucat.SerialIO;

using System.IO.Ports;
using System.Reflection;
using System.Text;

public sealed class SerialLineReaderTest
{
    private static readonly string ReceivePort = Environment.GetEnvironmentVariable("SERIALIO_TEST_RECV") ?? "COM7";
    private static readonly string SendPort = Environment.GetEnvironmentVariable("SERIALIO_TEST_SEND") ?? "COM8";

    private const int WaitTimeout = 5000;
    private const int SendWait = 100;
    private const int WaitValueTimeout = 5000;

    // ------------------------------------------------------------
    // Helper
    // ------------------------------------------------------------

    private static bool WaitForFieldValue<T>(object target, string fieldName, Func<T, bool> predicate)
    {
        var fieldInfo = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        if (fieldInfo is null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' not found on type '{target.GetType().Name}'");
        }

        var endTime = DateTime.UtcNow.AddMilliseconds(WaitValueTimeout);
        while (DateTime.UtcNow < endTime)
        {
            var value = (T?)fieldInfo.GetValue(target);
            if ((value is not null) && predicate(value))
            {
                return true;
            }
            Thread.Sleep(10);
        }

        return false;
    }

    private static bool WaitForStats(SerialLineReader reader, Func<SerialLineReader.Statistics, bool> predicate)
    {
        var endTime = DateTime.UtcNow.AddMilliseconds(WaitValueTimeout);
        while (DateTime.UtcNow < endTime)
        {
            if (predicate(reader.GetStatistics()))
            {
                return true;
            }
            Thread.Sleep(10);
        }

        return false;
    }

    // ------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------

    [Fact]
    public void TestNormalReceive()
    {
        // Normal receive rest

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 100);

        var list = new List<string>();
        using var event1 = new ManualResetEventSlim(false);
        using var event2 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [event1, event2];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // 1st data send
        sendPort.Write("Hello\n");
        event1.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        // 2nd data send
        sendPort.Write("World\n");
        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(2, list.Count);
        Assert.Equal("Hello", list[0]);
        Assert.Equal("World", list[1]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(2, stats.TotalLinesReceived); // 2 lines received
        Assert.Equal(12, stats.TotalBytesReceived); // "Hello\n" + "World\n" = 12
        Assert.Equal(0, stats.TotalOverflowCount); // No overflow
        Assert.Equal(0, stats.TotalEmptyLinesSkipped); // No empty lines
    }

    [Fact]
    public void TestBufferOverflow()
    {
        // Overflow test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 10);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);
        using var overflowEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent, overflowEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        reader.BufferOverflow += (_, _) =>
        {
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Setup

        // Send data that causes overflow
        sendPort.Write("ABCDEFGHIJKLMNO\n");
        overflowEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Single(list);
        Assert.Equal("GHIJKLMNO", list[0]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount >= 1); // Overflow occurred
        Assert.True(stats.TotalBytesDiscarded > 0); // Discarded data exists
    }

    [Fact]
    public void TestRingWrap()
    {
        // Ring buffer wrap-around test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 15);

        var list = new List<string>();
        using var event1 = new ManualResetEventSlim(false);
        using var event2 = new ManualResetEventSlim(false);
        using var event3 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [event1, event2, event3];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // In buffer boundary data send
        sendPort.Write("First\n");
        event1.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        // In buffer boundary data send
        sendPort.Write("Second\n");
        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        // Wrap around data send
        sendPort.Write("Third\n");
        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(3, list.Count);
        Assert.Equal("First", list[0]);
        Assert.Equal("Second", list[1]);
        Assert.Equal("Third", list[2]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.Equal(19, stats.TotalBytesReceived); // 6+7+6
    }

    [Fact]
    public void TestMultiByteDelimiter()
    {
        // Multi bytes terminator test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: "\r\n"u8.ToArray(),
            maxBufferSize: 50);

        var list = new List<string>();
        using var event1 = new ManualResetEventSlim(false);
        using var event2 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [event1, event2];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // 1st data send
        sendPort.Write("Line1\r\n");
        event1.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        // 2nd data send
        sendPort.Write("Line2\r\n");
        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(2, list.Count);
        Assert.Equal("Line1", list[0]);
        Assert.Equal("Line2", list[1]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(2, stats.TotalLinesReceived);
        Assert.Equal(14, stats.TotalBytesReceived); // "Line1\r\n" + "Line2\r\n" = 14
    }

    [Fact]
    public void TestSearchOptimization()
    {
        // Search optimization test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send split data to test searchStart optimization
        sendPort.Write("Partial");
        Thread.Sleep(SendWait);

        // Verify that search start position updated (should be greater than 0)
        Assert.True(WaitForFieldValue<int>(reader, "search", value => value > 0));

        sendPort.Write("Data");
        Thread.Sleep(SendWait);

        // Verify that search start position updated (should be greater than 0)
        Assert.True(WaitForFieldValue<int>(reader, "search", value => value > 7));

        // Send terminator
        sendPort.Write("Here\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Single(list);
        Assert.Equal("PartialDataHere", list[0]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
        Assert.Equal(16, stats.TotalBytesReceived);
    }

    [Fact]
    public void TestContinuousData()
    {
        // Continuous data reception test

        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 30);

        var list = new List<string>();
        using var event3 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, null, event3];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send continuous data
        sendPort.Write("Line1\nLine2\nLine3\n");
        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(3, list.Count);
        Assert.Equal("Line1", list[0]);
        Assert.Equal("Line2", list[1]);
        Assert.Equal("Line3", list[2]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.Equal(18, stats.TotalBytesReceived);
    }

    [Fact]
    public void TestEmptyLines()
    {
        // Empty line handling test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        var list = new List<string>();
        using var event2 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, event2];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send data with empty line
        sendPort.Write("Before\n\nAfter\n");
        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data (empty line should be skipped)
        Assert.Equal(2, list.Count);
        Assert.Equal("Before", list[0]);
        Assert.Equal("After", list[1]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(2, stats.TotalLinesReceived);
        Assert.Equal(1, stats.TotalEmptyLinesSkipped);
    }

    [Fact]
    public void TestStackAllocThreshold()
    {
        // stackalloc and ArrayPool threshold test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 2048);

        var list = new List<string>();
        using var event2 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, event2];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send small data (uses stackalloc)
        var smallData = new string('A', 500);
        sendPort.Write(smallData + "\n");
        Thread.Sleep(SendWait);

        // Send large data (uses ArrayPool)
        var largeData = new string('B', 600);
        sendPort.Write(largeData + "\n");

        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(2, list.Count);
        Assert.Equal(500, list[0].Length);
        Assert.Equal(600, list[1].Length);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(2, stats.TotalLinesReceived);
        Assert.Equal(1102, stats.TotalBytesReceived); // 501 + 601
    }

    [Fact]
    public void TestDelimiterAtBufferBoundary()
    {
        // Delimiter at buffer boundary test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);

        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 20);

        var list = new List<string>();
        using var event2 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, event2];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send data where delimiter is at boundary
        sendPort.Write("12345678901234567\n");
        Thread.Sleep(SendWait);

        // Send next data
        sendPort.Write("AB\n");

        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(2, list.Count);
        Assert.Equal("12345678901234567", list[0]);
        Assert.Equal("AB", list[1]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(2, stats.TotalLinesReceived);
    }

    [Fact]
    public void TestMultiByteDelimiterSplit()
    {
        // Multi-byte delimiter split reception test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);

        using var reader = new SerialLineReader(
            receivePort,
            delimiter: "\r\n"u8.ToArray(),
            maxBufferSize: 50);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send delimiter split across packets
        sendPort.Write("Test\r");
        Thread.Sleep(SendWait);
        sendPort.Write("\n");

        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Single(list);
        Assert.Equal("Test", list[0]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void TestMaxBufferSizeExactly()
    {
        // Exact buffer size data test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 10);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send data exactly matching buffer size
        sendPort.Write("123456789\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Single(list);
        Assert.Equal("123456789", list[0]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
        Assert.Equal(10, stats.TotalBytesReceived);
    }

    [Fact]
    public void TestRepeatedOverflow()
    {
        // Repeated overflow test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 10);

        var list = new List<string>();
        using var event3 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, null, event3];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send data causing repeated overflows
        sendPort.Write("AAAAAAAAAAAAAAAA\n");
        Thread.Sleep(SendWait);
        sendPort.Write("BBBBBBBBBBBBBBBB\n");
        Thread.Sleep(SendWait);
        sendPort.Write("CCCCCCCCCCCCCCCC\n");

        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data (truncated)
        Assert.Equal(3, list.Count);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount >= 3);
    }

    [Fact]
    public void TestDelimiterOnly()
    {
        // Delimiter-only test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);

        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        var list = new List<string>();
        ManualResetEventSlim?[] events = [];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send delimiter only (should be skipped)
        sendPort.Write("\n");
        Thread.Sleep(SendWait);

        sendPort.Close();

        // Assert

        // Assert no line received
        Assert.Empty(list);
        Assert.Equal(0, receivedCount);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(0, stats.TotalLinesReceived);
        Assert.Equal(1, stats.TotalEmptyLinesSkipped);
    }

    [Fact]
    public void TestLargeLineWithStackAlloc()
    {
        // Large data crossing ring boundary (ArrayPool usage test)

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 1024);

        var list = new List<string>();
        using var event2 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, event2];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send padding data
        var padding = new string('X', 900);
        sendPort.Write(padding + "\n");
        Thread.Sleep(SendWait);

        // Send large data (should use ArrayPool)
        var largeData = new string('Y', 600);
        sendPort.Write(largeData + "\n");

        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(2, list.Count);
        Assert.Equal(900, list[0].Length);
        Assert.Equal(600, list[1].Length);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(2, stats.TotalLinesReceived);
    }

    [Fact]
    public void TestSearchStartAtBoundary()
    {
        // Search start at boundary test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 20);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send data near boundary
        sendPort.Write("12345678901234567");
        Thread.Sleep(SendWait);

        // Send remaining data
        sendPort.Write("8\n");

        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Single(list);
        Assert.Equal("123456789012345678", list[0]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void TestAlternatingHeadTailPositions()
    {
        // Alternating head/tail positions test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 30);

        var list = new List<string>();
        using var event5 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, null, null, null, event5];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send varying length data to alternate head/tail positions
        sendPort.Write("A\n");
        Thread.Sleep(SendWait);
        sendPort.Write("BBBBBBBBBB\n");
        Thread.Sleep(SendWait);
        sendPort.Write("CCC\n");
        Thread.Sleep(SendWait);
        sendPort.Write("DDDDDDDDDDDDDD\n");
        Thread.Sleep(SendWait);
        sendPort.Write("EE\n");

        event5.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(5, list.Count);
        Assert.Equal("A", list[0]);
        Assert.Equal("BBBBBBBBBB", list[1]);
        Assert.Equal("CCC", list[2]);
        Assert.Equal("DDDDDDDDDDDDDD", list[3]);
        Assert.Equal("EE", list[4]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(5, stats.TotalLinesReceived);
    }

    [Fact]
    public void TestPartialDelimiterAtEnd()
    {
        // Partial delimiter at buffer end test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: "\r\n\r\n"u8.ToArray(),
            maxBufferSize: 50);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send data with multi-byte delimiter
        sendPort.Write("Data\r\n\r\n");

        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Single(list);
        Assert.Equal("Data", list[0]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void TestConsecutiveDelimiters()
    {
        // Consecutive delimiters test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        var list = new List<string>();
        ManualResetEventSlim?[] events = [];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send consecutive delimiters (all should be skipped)
        sendPort.Write("\n\n\n");
        Thread.Sleep(100);

        sendPort.Close();

        // Assert

        // Assert no line received
        Assert.Empty(list);
        Assert.Equal(0, receivedCount);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(0, stats.TotalLinesReceived);
        Assert.Equal(3, stats.TotalEmptyLinesSkipped);
    }

    [Fact]
    public void TestSingleByteReads()
    {
        // Single byte read test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send data one byte at a time
        var data = "Test\n";
        foreach (var c in data)
        {
            sendPort.Write(c.ToString());
            Thread.Sleep(SendWait);
        }

        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Single(list);
        Assert.Equal("Test", list[0]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void TestFullBufferNoDelimiter()
    {
        // Full buffer without delimiter test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 10);

        var list = new List<string>();
        ManualResetEventSlim?[] events = [];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Fill buffer without delimiter
        sendPort.Write("1234567890");
        Thread.Sleep(SendWait);

        // Send more data (should overflow)
        sendPort.Write("ABCDE");
        Thread.Sleep(100);

        sendPort.Close();

        // Assert

        // Assert no line received
        Assert.Empty(list);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(0, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount > 0);
    }

    [Fact]
    public void TestOverflowThenDelimiter()
    {
        // Overflow followed by delimiter test (incomplete data)

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 10);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);
        using var overflowEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent, overflowEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        reader.BufferOverflow += (_, _) =>
        {
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send data causing overflow then delimiter
        sendPort.Write("ABCDEFGHIJKLMNOPQRSTUVWXYZ\n");
        overflowEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data (truncated)
        Assert.Single(list);
        Assert.True(list[0].Length <= 10);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount >= 1);
        Assert.True(stats.TotalBytesDiscarded > 0);
    }

    [Fact]
    public void TestContinuousOverflowNoDelimiter()
    {
        // Continuous overflow without delimiter test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 10);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send continuous data without delimiters (causes overflows)
        sendPort.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        Thread.Sleep(SendWait);
        sendPort.Write("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        Thread.Sleep(SendWait);

        // Finally send delimiter
        sendPort.Write("CCCCCCCCCC\n");

        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data (truncated)
        Assert.Single(list);
        Assert.True(list[0].Length <= 10);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount > 0);
        Assert.True(stats.TotalBytesDiscarded > 0);
    }

    [Fact]
    public void TestOverflowThenMultipleLines()
    {
        // Overflow followed by multiple lines test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 15);

        var list = new List<string>();
        using var event3 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, null, event3];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send data causing overflow
        sendPort.Write("VERYLONGDATAAAAAAAAAAAA\n");
        Thread.Sleep(SendWait);

        // Send normal data after overflow
        sendPort.Write("OK1\n");
        Thread.Sleep(SendWait);
        sendPort.Write("OK2\n");

        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(3, list.Count);
        Assert.Equal("OK1", list[1]);
        Assert.Equal("OK2", list[2]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount >= 1);
    }

    [Fact]
    public void TestStatistics()
    {
        // Statistics verification test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);

        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 20);

        using var event3 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, null, event3];
        var receivedCount = 0;

        reader.LineReceived += (_, _) =>
        {
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send various data patterns
        sendPort.Write("Line1\n");
        Thread.Sleep(SendWait);
        sendPort.Write("\n");
        Thread.Sleep(SendWait);
        sendPort.Write("Line2\n");
        Thread.Sleep(SendWait);
        sendPort.Write("VERYLONGDATAAAAAAAAAAAAAAAAAA\n");
        Thread.Sleep(SendWait);
        sendPort.Write("Line3\n");

        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert statistics
        var stats = reader.GetStatistics();

        Assert.True(stats.TotalLinesReceived >= 3);
        Assert.True(stats.TotalBytesReceived > 0);
        Assert.True(stats.TotalOverflowCount >= 1);
        Assert.True(stats.TotalBytesDiscarded > 0);
        Assert.True(stats.TotalEmptyLinesSkipped >= 1);
    }

    [Fact]
    public void TestDiscardBufferBasic()
    {
        // Basic buffer discard test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            lineEvent.Set();
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send partial data
        sendPort.Write("PartialData");
        Thread.Sleep(100);

        // Discard buffer
        var discarded = reader.DiscardBuffer();
        Assert.Equal(11, discarded);
        Assert.Equal(0, reader.CurrentBufferUsage);

        // Send new complete line
        sendPort.Write("NewLine\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data (only new line)
        Assert.Single(list);
        Assert.Equal("NewLine", list[0]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
        Assert.Equal(1, stats.TotalDiscardCount);
        Assert.Equal(11, stats.TotalBytesDiscarded);
    }

    [Fact]
    public void TestDiscardBufferEmpty()
    {
        // Empty buffer discard test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Discard empty buffer
        var discarded = reader.DiscardBuffer();

        sendPort.Close();

        // Assert

        // Assert nothing discarded
        Assert.Equal(0, discarded);
        Assert.Equal(0, reader.CurrentBufferUsage);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalDiscardCount);
        Assert.Equal(0, stats.TotalBytesDiscarded);
    }

    [Fact]
    public void TestDiscardBufferWithRingWrap()
    {
        // Buffer discard with ring wrap test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 20);

        var list = new List<string>();
        using var event3 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, null, event3];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Create ring wrap condition
        sendPort.Write("First\n");
        Thread.Sleep(SendWait);
        sendPort.Write("Second\n");
        Thread.Sleep(SendWait);

        // Send partial data
        sendPort.Write("PartialThird");
        Thread.Sleep(100);

        // Discard buffer
        var discarded = reader.DiscardBuffer();
        Assert.Equal(12, discarded);

        // Send new data
        sendPort.Write("NewData\n");
        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(3, list.Count);
        Assert.Equal("First", list[0]);
        Assert.Equal("Second", list[1]);
        Assert.Equal("NewData", list[2]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.Equal(1, stats.TotalDiscardCount);
        Assert.Equal(12, stats.TotalBytesDiscarded);
    }

    [Fact]
    public void TestDiscardBufferStatistics()
    {
        // Discard buffer statistics test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 30);

        using var lineEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, _) =>
        {
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Multiple discard operations with varying data sizes
        sendPort.Write("Data1");
        Thread.Sleep(SendWait);
        var d1 = reader.DiscardBuffer();

        sendPort.Write("Data2Data2");
        Thread.Sleep(SendWait);
        var d2 = reader.DiscardBuffer();

        sendPort.Write("Data3Data3Data3");
        Thread.Sleep(SendWait);
        var d3 = reader.DiscardBuffer();

        // Send final complete line
        sendPort.Write("FinalLine\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert discard amounts
        Assert.Equal(5, d1);
        Assert.Equal(10, d2);
        Assert.Equal(15, d3);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalDiscardCount);
        Assert.Equal(30, stats.TotalBytesDiscarded);
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void TestDiscardBufferAfterPartialData()
    {
        // Buffer discard after partial data reception test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        var list = new List<string>();
        using var lineEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send partial data in multiple chunks
        sendPort.Write("Part");
        Thread.Sleep(SendWait);
        sendPort.Write("ial");
        Thread.Sleep(SendWait);
        sendPort.Write("Data");
        Thread.Sleep(SendWait);

        // Discard accumulated partial data
        var discarded = reader.DiscardBuffer();
        Assert.Equal(11, discarded);

        // Send complete line
        sendPort.Write("CompleteLine\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Single(list);
        Assert.Equal("CompleteLine", list[0]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void TestDiscardBufferMultipleDiscards()
    {
        // Multiple consecutive discards test
        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);

        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Multiple discards on empty buffer
        var d1 = reader.DiscardBuffer();
        var d2 = reader.DiscardBuffer();
        var d3 = reader.DiscardBuffer();

        Assert.Equal(0, d1);
        Assert.Equal(0, d2);
        Assert.Equal(0, d3);

        // Send data and discard multiple times
        sendPort.Write("Test");
        Thread.Sleep(SendWait);
        var d4 = reader.DiscardBuffer();
        var d5 = reader.DiscardBuffer();

        Assert.Equal(4, d4);
        Assert.Equal(0, d5);

        sendPort.Close();

        // Assert

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(5, stats.TotalDiscardCount);
        Assert.Equal(4, stats.TotalBytesDiscarded);
    }

    [Fact]
    public void TestDiscardBufferDuringReceive()
    {
        // Buffer discard during reception test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        var list = new List<string>();
        using var event3 = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [null, null, event3];
        var receivedCount = 0;
        var shouldDiscard = false;

        reader.LineReceived += (_, lineBytes) =>
        {
            var line = Encoding.UTF8.GetString(lineBytes);
            list.Add(line);

            if (line.Contains("ERROR", StringComparison.Ordinal))
            {
                shouldDiscard = true;
            }

            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send normal data
        sendPort.Write("NormalLine\n");
        Thread.Sleep(SendWait);

        // Send error line
        sendPort.Write("ERROR_LINE\n");
        Thread.Sleep(SendWait);

        // Discard buffer after error
        if (shouldDiscard)
        {
            sendPort.Write("PartialAfterError");
            Thread.Sleep(SendWait);

            var discarded = reader.DiscardBuffer();
            Assert.Equal(17, discarded);
        }

        // Send recovery line
        sendPort.Write("RecoveryLine\n");
        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        // Assert

        // Assert received data
        Assert.Equal(3, list.Count);
        Assert.Equal("NormalLine", list[0]);
        Assert.Equal("ERROR_LINE", list[1]);
        Assert.Equal("RecoveryLine", list[2]);

        // Assert statistics
        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
    }

    [Fact]
    public void TestDiscardBufferWithPeakUsage()
    {
        // Buffer discard with peak usage tracking test

        // Setup
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        using var lineEvent = new ManualResetEventSlim(false);
        ManualResetEventSlim?[] events = [lineEvent];
        var receivedCount = 0;

        reader.LineReceived += (_, _) =>
        {
            events[receivedCount]?.Set();
            receivedCount++;
        };

        receivePort.Open();
        sendPort.Open();

        // Start Test

        // Send large data to establish peak
        var largeData = new string('X', 40);
        sendPort.Write(largeData);
        Thread.Sleep(SendWait);

        // Verify current and peak usage
        var stats1 = reader.GetStatistics();
        Assert.Equal(40, stats1.CurrentBufferUsage);
        Assert.Equal(40, stats1.PeakBufferUsage);

        // Discard buffer
        var discarded = reader.DiscardBuffer();
        Assert.Equal(40, discarded);

        // Verify peak persists after discard
        var stats2 = reader.GetStatistics();
        Assert.Equal(0, stats2.CurrentBufferUsage);
        Assert.Equal(40, stats2.PeakBufferUsage);

        // Send small data
        sendPort.Write("Small\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        // Verify peak still maintained
        var stats3 = reader.GetStatistics();
        Assert.Equal(40, stats3.PeakBufferUsage);

        sendPort.Close();
    }

    [Fact]
    public void TestBurstDouble()
    {
        // Burst 2x: maxBufferSize=16, threshold=32
        // junk16 (no delimiter) + "abc\ndefgh\nijklm\n" (16 bytes) = 32 bytes total
        // Expected: lines=["abc","defgh","ijklm"], received=32, discarded=16, overflow=1, lines=3

        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 16);

        var lines = new List<string>();
        reader.LineReceived += (_, lineBytes) =>
        {
            lines.Add(Encoding.UTF8.GetString(lineBytes));
        };

        receivePort.ReceivedBytesThreshold = 32;
        receivePort.Open();
        sendPort.Open();

        // Send exactly 32 bytes in one write: 16 junk (no delimiter) + 16 bytes with delimiters
        var junk16 = "ABCDEFGHIJKLMNOP"; // 16 bytes, no '\n'
        var tail16 = "abc\ndefgh\nijklm\n"; // 16 bytes
        Assert.Equal(16, Encoding.UTF8.GetByteCount(junk16));
        Assert.Equal(16, Encoding.UTF8.GetByteCount(tail16));
        sendPort.Write(junk16 + tail16);

        // Wait until TotalBytesReceived == 32
        Assert.True(WaitForStats(reader, s => s.TotalBytesReceived >= 32));

        sendPort.Close();

        var stats = reader.GetStatistics();
        Assert.Equal(32, stats.TotalBytesReceived);
        Assert.Equal(16, stats.TotalBytesDiscarded);
        Assert.Equal(1, stats.TotalOverflowCount);
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.Equal(0, stats.CurrentBufferUsage);
        Assert.Equal(3, lines.Count);
        Assert.Equal("abc", lines[0]);
        Assert.Equal("defgh", lines[1]);
        Assert.Equal("ijklm", lines[2]);
    }

    [Fact]
    public void TestBurstTwoPointFive()
    {
        // Burst 2.5x: maxBufferSize=16, threshold=40
        // junk24 (no delimiter) + "abc\ndefgh\nijklm\n" (16 bytes) = 40 bytes total
        // Expected: discarded=24, received=40, lines same as above

        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 16);

        var lines = new List<string>();
        reader.LineReceived += (_, lineBytes) =>
        {
            lines.Add(Encoding.UTF8.GetString(lineBytes));
        };

        receivePort.ReceivedBytesThreshold = 40;
        receivePort.Open();
        sendPort.Open();

        // Send exactly 40 bytes: 24 junk + 16 bytes with delimiters
        var junk24 = "ABCDEFGHIJKLMNOPQRSTUVWX"; // 24 bytes, no '\n'
        var tail16 = "abc\ndefgh\nijklm\n"; // 16 bytes
        Assert.Equal(24, Encoding.UTF8.GetByteCount(junk24));
        Assert.Equal(16, Encoding.UTF8.GetByteCount(tail16));
        sendPort.Write(junk24 + tail16);

        Assert.True(WaitForStats(reader, s => s.TotalBytesReceived >= 40));

        sendPort.Close();

        var stats = reader.GetStatistics();
        Assert.Equal(40, stats.TotalBytesReceived);
        Assert.Equal(24, stats.TotalBytesDiscarded);
        Assert.Equal(1, stats.TotalOverflowCount);
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.Equal(0, stats.CurrentBufferUsage);
        Assert.Equal(3, lines.Count);
        Assert.Equal("abc", lines[0]);
        Assert.Equal("defgh", lines[1]);
        Assert.Equal("ijklm", lines[2]);
    }

    [Fact]
    public void TestBurstWithExistingData()
    {
        // Burst with pre-existing data: maxBufferSize=16
        // Phase1: threshold=1, send 10 bytes junk (no delimiter), wait for buffer usage==10
        // Phase2: threshold=40, send 40 bytes (junk24 + "xy\nhello\nworlds\n")
        // Expected: discarded=34 (10 existing + 24 drained), received=50, lines=["xy","hello","worlds"], overflow=1

        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);
        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 16);

        var lines = new List<string>();
        reader.LineReceived += (_, lineBytes) =>
        {
            lines.Add(Encoding.UTF8.GetString(lineBytes));
        };

        receivePort.ReceivedBytesThreshold = 1;
        receivePort.Open();
        sendPort.Open();

        // Phase 1: send 10 bytes junk, wait for it to be buffered
        var junk10 = "ABCDEFGHIJ"; // 10 bytes, no '\n'
        Assert.Equal(10, Encoding.UTF8.GetByteCount(junk10));
        sendPort.Write(junk10);

        // Wait until CurrentBufferUsage == 10
        Assert.True(WaitForStats(reader, s => s.CurrentBufferUsage == 10));

        // Phase 2: switch threshold and send 40 bytes
        receivePort.ReceivedBytesThreshold = 40;
        var junk24 = "ABCDEFGHIJKLMNOPQRSTUVWX"; // 24 bytes, no '\n'
        var tail16 = "xy\nhello\nworlds\n";         // 16 bytes
        Assert.Equal(24, Encoding.UTF8.GetByteCount(junk24));
        Assert.Equal(16, Encoding.UTF8.GetByteCount(tail16));
        sendPort.Write(junk24 + tail16);

        // Wait until TotalBytesReceived == 50
        Assert.True(WaitForStats(reader, s => s.TotalBytesReceived >= 50));

        sendPort.Close();

        var stats = reader.GetStatistics();
        Assert.Equal(50, stats.TotalBytesReceived);
        Assert.Equal(34, stats.TotalBytesDiscarded);
        Assert.Equal(1, stats.TotalOverflowCount);
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.Equal(0, stats.CurrentBufferUsage);
        Assert.Equal(3, lines.Count);
        Assert.Equal("xy", lines[0]);
        Assert.Equal("hello", lines[1]);
        Assert.Equal("worlds", lines[2]);
    }

    [Fact]
    public void TestDisposeRace()
    {
        // Dispose race: loop ~20 times, send while disposing — must not crash

        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);

        receivePort.Open();
        sendPort.Open();

        // Fixed jitter sequence (0..4 ms) to avoid CA5394 (no security requirement here)
        int[] jitterMs = [0, 1, 2, 3, 4, 0, 2, 1, 3, 4, 0, 1, 2, 3, 4, 0, 2, 1, 3, 4];

        for (var i = 0; i < 20; i++)
        {
            var reader = new SerialLineReader(
                receivePort,
                delimiter: [(byte)'\n'],
                maxBufferSize: 64);

            // Send in background while we sleep briefly then dispose
            var sendThread = new Thread(() =>
            {
                try
                {
                    for (var j = 0; j < 10; j++)
                    {
                        sendPort.Write("HELLO\n");
                    }
                }
                catch (InvalidOperationException)
                {
                    // port closed during send is acceptable
                }
            });

            sendThread.Start();

            // Fixed tiny sleep before dispose
            Thread.Sleep(jitterMs[i]);

            reader.Dispose();
            sendThread.Join(500);
        }

        sendPort.Close();
        receivePort.Close();

        // If we get here without crash/hang, test passes
        Assert.True(true);
    }

    [Fact]
    public void TestClosedPortHandlerError()
    {
        // Fire OnDataReceived on a reader whose port is closed — expect no exception leak,
        // TotalReceiveErrors should become 1.

        using var receivePort = new SerialPort(ReceivePort, 9600);
        // Do NOT open the port — reading BytesToRead on a closed port throws InvalidOperationException
        var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 64);

        // Build SerialDataReceivedEventArgs via non-public constructor
        var args = Activator.CreateInstance(
            typeof(SerialDataReceivedEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [SerialData.Chars],
            culture: null)!;

        // Invoke the private handler via reflection — must not throw
        var method = typeof(SerialLineReader).GetMethod(
            "OnDataReceived",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(reader, [reader, args]);

        Assert.Equal(1, reader.TotalReceiveErrors);

        reader.Dispose();
    }

    [Fact]
    public void TestConstructorValidation()
    {
        // maxBufferSize=0 must throw ArgumentOutOfRangeException
        using var port = new SerialPort(ReceivePort, 9600);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SerialLineReader(port, delimiter: [(byte)'\n'], maxBufferSize: 0));

        // maxBufferSize=1 with 2-byte delimiter must throw ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SerialLineReader(port, delimiter: "\r\n"u8.ToArray(), maxBufferSize: 1));
    }
}
