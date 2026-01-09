// ReSharper disable AccessToDisposedClosure
// ReSharper disable StringLiteralTypo
#pragma warning disable CA1707
namespace Mofucat.SerialIO;

using System.IO.Ports;
using System.Reflection;
using System.Text;

public sealed class SerialLineReaderTest
{
    private const string ReceivePort = "COM7";
    private const string SendPort = "COM8";

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

    // ------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------

    [Fact]
    public void Test_NormalReceive()
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
    public void Test_BufferOverflow()
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
    public void Test_RingWrap()
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
    public void Test_MultiByteDelimiter()
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
    public void Test_SearchOptimization()
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

    // TODO comment update
    [Fact]
    public void Test6_ContinuousData()
    {
        // 連続したデータ受信
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

        sendPort.Write("Line1\nLine2\nLine3\n");
        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(3, list.Count);
        Assert.Equal("Line1", list[0]);
        Assert.Equal("Line2", list[1]);
        Assert.Equal("Line3", list[2]);

        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.Equal(18, stats.TotalBytesReceived);
    }

    [Fact]
    public void Test7_EmptyLines()
    {
        // 空行の処理
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

        sendPort.Write("Before\n\nAfter\n");
        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(2, list.Count);
        Assert.Equal("Before", list[0]);
        Assert.Equal("After", list[1]);

        var stats = reader.GetStatistics();
        Assert.Equal(2, stats.TotalLinesReceived);
        Assert.Equal(1, stats.TotalEmptyLinesSkipped);
    }

    [Fact]
    public void Test8_StackAllocThreshold()
    {
        // stackallocとArrayPoolの閾値テスト
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

        var smallData = new string('A', 500);
        sendPort.Write(smallData + "\n");
        Thread.Sleep(SendWait);

        var largeData = new string('B', 600);
        sendPort.Write(largeData + "\n");

        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(2, list.Count);
        Assert.Equal(500, list[0].Length);
        Assert.Equal(600, list[1].Length);

        var stats = reader.GetStatistics();
        Assert.Equal(2, stats.TotalLinesReceived);
        Assert.Equal(1102, stats.TotalBytesReceived); // 501 + 601
    }

    [Fact]
    public void Test9_DelimiterAtBufferBoundary()
    {
        // 終端文字列がバッファ境界に位置するケース
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

        sendPort.Write("12345678901234567\n");
        Thread.Sleep(SendWait);
        sendPort.Write("AB\n");

        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(2, list.Count);
        Assert.Equal("12345678901234567", list[0]);
        Assert.Equal("AB", list[1]);

        var stats = reader.GetStatistics();
        Assert.Equal(2, stats.TotalLinesReceived);
    }

    [Fact]
    public void Test10_MultiByteDelimiterSplit()
    {
        // 複数バイト終端文字列が分割受信されるケース
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

        sendPort.Write("Test\r");
        Thread.Sleep(50);
        sendPort.Write("\n");

        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Single(list);
        Assert.Equal("Test", list[0]);

        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void Test11_MaxBufferSizeExactly()
    {
        // バッファサイズぴったりのデータ
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

        sendPort.Write("123456789\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Single(list);
        Assert.Equal("123456789", list[0]);

        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
        Assert.Equal(10, stats.TotalBytesReceived);
    }

    [Fact]
    public void Test12_RepeatedOverflow()
    {
        // 連続的なオーバーフロー
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

        sendPort.Write("AAAAAAAAAAAAAAAA\n");
        Thread.Sleep(SendWait);
        sendPort.Write("BBBBBBBBBBBBBBBB\n");
        Thread.Sleep(SendWait);
        sendPort.Write("CCCCCCCCCCCCCCCC\n");

        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(3, list.Count);

        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount >= 3);
    }

    [Fact]
    public void Test13_DelimiterOnly()
    {
        // 終端文字列のみ
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

        sendPort.Write("\n");
        Thread.Sleep(100);

        sendPort.Close();

        Assert.Empty(list);
        Assert.Equal(0, receivedCount);

        var stats = reader.GetStatistics();
        Assert.Equal(0, stats.TotalLinesReceived);
        Assert.Equal(1, stats.TotalEmptyLinesSkipped);
    }

    [Fact]
    public void Test14_LargeLineWithStackAlloc()
    {
        // リング境界越えで大きいデータ（ArrayPool使用確認）
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

        var padding = new string('X', 900);
        sendPort.Write(padding + "\n");
        Thread.Sleep(SendWait);

        var largeData = new string('Y', 600);
        sendPort.Write(largeData + "\n");

        event2.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(2, list.Count);
        Assert.Equal(900, list[0].Length);
        Assert.Equal(600, list[1].Length);

        var stats = reader.GetStatistics();
        Assert.Equal(2, stats.TotalLinesReceived);
    }

    [Fact]
    public void Test15_SearchStartAtBoundary()
    {
        // searchStartが境界付近にあるケース
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

        sendPort.Write("12345678901234567");
        Thread.Sleep(SendWait);
        sendPort.Write("8\n");

        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Single(list);
        Assert.Equal("123456789012345678", list[0]);

        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void Test16_AlternatingHeadTailPositions()
    {
        // head/tailの位置が交互に変わるケース
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

        sendPort.Write("A\n");
        Thread.Sleep(50);
        sendPort.Write("BBBBBBBBBB\n");
        Thread.Sleep(50);
        sendPort.Write("CCC\n");
        Thread.Sleep(50);
        sendPort.Write("DDDDDDDDDDDDDD\n");
        Thread.Sleep(50);
        sendPort.Write("EE\n");

        event5.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(5, list.Count);
        Assert.Equal("A", list[0]);
        Assert.Equal("BBBBBBBBBB", list[1]);
        Assert.Equal("CCC", list[2]);
        Assert.Equal("DDDDDDDDDDDDDD", list[3]);
        Assert.Equal("EE", list[4]);

        var stats = reader.GetStatistics();
        Assert.Equal(5, stats.TotalLinesReceived);
    }

    [Fact]
    public void Test17_PartialDelimiterAtEnd()
    {
        // 終端の一部がバッファ末尾にあるケース
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

        sendPort.Write("Data\r\n\r\n");

        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Single(list);
        Assert.Equal("Data", list[0]);

        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void Test18_ConsecutiveDelimiters()
    {
        // 連続する終端文字列
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

        sendPort.Write("\n\n\n");
        Thread.Sleep(100);

        sendPort.Close();

        Assert.Empty(list);
        Assert.Equal(0, receivedCount);

        var stats = reader.GetStatistics();
        Assert.Equal(0, stats.TotalLinesReceived);
        Assert.Equal(3, stats.TotalEmptyLinesSkipped);
    }

    [Fact]
    public void Test19_SingleByteReads()
    {
        // 1バイトずつ受信
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

        var data = "Test\n";
        foreach (var c in data)
        {
            sendPort.Write(c.ToString());
            Thread.Sleep(SendWait);
        }

        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Single(list);
        Assert.Equal("Test", list[0]);

        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void Test20_FullBufferNoDelimiter()
    {
        // バッファ満杯で終端なし
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

        sendPort.Write("1234567890");
        Thread.Sleep(SendWait);
        sendPort.Write("ABCDE");
        Thread.Sleep(100);

        sendPort.Close();

        Assert.Empty(list);

        var stats = reader.GetStatistics();
        Assert.Equal(0, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount > 0);
    }

    [Fact]
    public void Test21_OverflowThenDelimiter()
    {
        // バッファオーバーフロー後に終端受信（不完全データ）
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

        sendPort.Write("ABCDEFGHIJKLMNOPQRSTUVWXYZ\n");
        overflowEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Single(list);
        Assert.True(list[0].Length <= 10);

        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount >= 1);
        Assert.True(stats.TotalBytesDiscarded > 0);
    }

    [Fact]
    public void Test22_ContinuousOverflowNoDelimiter()
    {
        // 終端なしの連続オーバーフロー
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

        sendPort.Write("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        Thread.Sleep(SendWait);
        sendPort.Write("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        Thread.Sleep(SendWait);
        sendPort.Write("CCCCCCCCCC\n");

        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Single(list);
        Assert.True(list[0].Length <= 10);

        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount > 0);
        Assert.True(stats.TotalBytesDiscarded > 0);
    }

    [Fact]
    public void Test23_OverflowThenMultipleLines()
    {
        // オーバーフロー後の複数行
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

        sendPort.Write("VERYLONGDATAAAAAAAAAAAA\n");
        Thread.Sleep(SendWait);
        sendPort.Write("OK1\n");
        Thread.Sleep(SendWait);
        sendPort.Write("OK2\n");

        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(3, list.Count);
        Assert.Equal("OK1", list[1]);
        Assert.Equal("OK2", list[2]);

        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.True(stats.TotalOverflowCount >= 1);
    }

    [Fact]
    public void Test24_Statistics()
    {
        // 統計情報の確認
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

        var stats = reader.GetStatistics();

        Assert.True(stats.TotalLinesReceived >= 3);
        Assert.True(stats.TotalBytesReceived > 0);
        Assert.True(stats.TotalOverflowCount >= 1);
        Assert.True(stats.TotalBytesDiscarded > 0);
        Assert.True(stats.TotalEmptyLinesSkipped >= 1);
    }

    [Fact]
    public void Test25_DiscardBuffer_Basic()
    {
        // 基本的なバッファ破棄
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

        sendPort.Write("PartialData");
        Thread.Sleep(100);

        var discarded = reader.DiscardBuffer();
        Assert.Equal(11, discarded);
        Assert.Equal(0, reader.CurrentBufferUsage);

        sendPort.Write("NewLine\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Single(list);
        Assert.Equal("NewLine", list[0]);

        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
        Assert.Equal(1, stats.TotalDiscardCount);
        Assert.Equal(11, stats.TotalBytesDiscarded);
    }

    [Fact]
    public void Test26_DiscardBuffer_Empty()
    {
        // 空バッファの破棄
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);

        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        receivePort.Open();
        sendPort.Open();

        var discarded = reader.DiscardBuffer();

        sendPort.Close();

        Assert.Equal(0, discarded);
        Assert.Equal(0, reader.CurrentBufferUsage);

        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalDiscardCount);
        Assert.Equal(0, stats.TotalBytesDiscarded);
    }

    [Fact]
    public void Test27_DiscardBuffer_WithRingWrap()
    {
        // リング状態でのバッファ破棄
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

        sendPort.Write("First\n");
        Thread.Sleep(SendWait);
        sendPort.Write("Second\n");
        Thread.Sleep(SendWait);
        sendPort.Write("PartialThird");
        Thread.Sleep(100);

        var discarded = reader.DiscardBuffer();
        Assert.Equal(12, discarded);

        sendPort.Write("NewData\n");
        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(3, list.Count);
        Assert.Equal("First", list[0]);
        Assert.Equal("Second", list[1]);
        Assert.Equal("NewData", list[2]);

        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
        Assert.Equal(1, stats.TotalDiscardCount);
        Assert.Equal(12, stats.TotalBytesDiscarded);
    }

    [Fact]
    public void Test28_DiscardBuffer_Statistics()
    {
        // 統計情報の確認
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

        sendPort.Write("Data1");
        Thread.Sleep(50);
        var d1 = reader.DiscardBuffer();

        sendPort.Write("Data2Data2");
        Thread.Sleep(50);
        var d2 = reader.DiscardBuffer();

        sendPort.Write("Data3Data3Data3");
        Thread.Sleep(50);
        var d3 = reader.DiscardBuffer();

        sendPort.Write("FinalLine\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(5, d1);
        Assert.Equal(10, d2);
        Assert.Equal(15, d3);

        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalDiscardCount);
        Assert.Equal(30, stats.TotalBytesDiscarded);
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void Test29_DiscardBuffer_AfterPartialData()
    {
        // 部分データ受信後の破棄
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

        sendPort.Write("Part");
        Thread.Sleep(50);
        sendPort.Write("ial");
        Thread.Sleep(50);
        sendPort.Write("Data");
        Thread.Sleep(50);

        var discarded = reader.DiscardBuffer();
        Assert.Equal(11, discarded);

        sendPort.Write("CompleteLine\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Single(list);
        Assert.Equal("CompleteLine", list[0]);

        var stats = reader.GetStatistics();
        Assert.Equal(1, stats.TotalLinesReceived);
    }

    [Fact]
    public void Test30_DiscardBuffer_MultipleDiscards()
    {
        // 連続的な破棄
        using var receivePort = new SerialPort(ReceivePort, 9600);
        using var sendPort = new SerialPort(SendPort, 9600);

        using var reader = new SerialLineReader(
            receivePort,
            delimiter: [(byte)'\n'],
            maxBufferSize: 50);

        receivePort.Open();
        sendPort.Open();

        var d1 = reader.DiscardBuffer();
        var d2 = reader.DiscardBuffer();
        var d3 = reader.DiscardBuffer();

        Assert.Equal(0, d1);
        Assert.Equal(0, d2);
        Assert.Equal(0, d3);

        sendPort.Write("Test");
        Thread.Sleep(50);
        var d4 = reader.DiscardBuffer();
        var d5 = reader.DiscardBuffer();

        Assert.Equal(4, d4);
        Assert.Equal(0, d5);

        sendPort.Close();

        var stats = reader.GetStatistics();
        Assert.Equal(5, stats.TotalDiscardCount);
        Assert.Equal(4, stats.TotalBytesDiscarded);
    }

    [Fact]
    public void Test31_DiscardBuffer_DuringReceive()
    {
        // 受信中の破棄
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

        sendPort.Write("NormalLine\n");
        Thread.Sleep(SendWait);

        sendPort.Write("ERROR_LINE\n");
        Thread.Sleep(100);

        if (shouldDiscard)
        {
            sendPort.Write("PartialAfterError");
            Thread.Sleep(50);

            var discarded = reader.DiscardBuffer();
            Assert.Equal(17, discarded);
        }

        sendPort.Write("RecoveryLine\n");
        event3.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        sendPort.Close();

        Assert.Equal(3, list.Count);
        Assert.Equal("NormalLine", list[0]);
        Assert.Equal("ERROR_LINE", list[1]);
        Assert.Equal("RecoveryLine", list[2]);

        var stats = reader.GetStatistics();
        Assert.Equal(3, stats.TotalLinesReceived);
    }

    [Fact]
    public void Test32_DiscardBuffer_WithPeakUsage()
    {
        // ピーク使用量との関係
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

        var largeData = new string('X', 40);
        sendPort.Write(largeData);
        Thread.Sleep(100);

        var stats1 = reader.GetStatistics();
        Assert.Equal(40, stats1.CurrentBufferUsage);
        Assert.Equal(40, stats1.PeakBufferUsage);

        var discarded = reader.DiscardBuffer();
        Assert.Equal(40, discarded);

        var stats2 = reader.GetStatistics();
        Assert.Equal(0, stats2.CurrentBufferUsage);
        Assert.Equal(40, stats2.PeakBufferUsage);

        sendPort.Write("Small\n");
        lineEvent.Wait(TimeSpan.FromMilliseconds(WaitTimeout), TestContext.Current.CancellationToken);

        var stats3 = reader.GetStatistics();
        Assert.Equal(40, stats3.PeakBufferUsage);

        sendPort.Close();
    }
}
