namespace Mofucat.SerialIO;

using System.Buffers;
using System.Diagnostics;
using System.IO.Ports;

public sealed class SerialLineReader : IDisposable
{
#pragma warning disable CA1003
    public event EventHandler<ReadOnlySpan<byte>>? LineReceived;
#pragma warning restore CA1003

#pragma warning disable CA1003
    public event EventHandler<int>? BufferOverflow;
#pragma warning restore CA1003

    private const int StackAllocThreshold = 512;

    private readonly SerialPort serialPort;
    private readonly Lock sync = new();
    private readonly byte[] delimiter;
    private readonly int maxBufferSize;
    private readonly bool ownsSerialPort;

    private int disposed;

    private byte[] buffer;
    private int head;  // 読み取り開始位置
    private int tail;  // 書き込み位置
    private int count; // バッファ内のデータ数
    private int searchStart; // 次回の終端検索開始位置（headからの相対位置）

    // Statics

    private long totalLinesReceived;
    private long totalBytesReceived;
    private long totalOverflowCount;
    private long totalBytesDiscarded;
    private long totalEmptyLinesSkipped;
    private int peakBufferUsage;
    private long totalDiscardCount;

    // ReSharper disable ConvertToAutoProperty
    public long TotalLinesReceived => totalLinesReceived;

    public long TotalBytesReceived => totalBytesReceived;

    public long TotalOverflowCount => totalOverflowCount;

    public long TotalBytesDiscarded => totalBytesDiscarded;

    public long TotalEmptyLinesSkipped => totalEmptyLinesSkipped;

    public long TotalDiscardCount => totalDiscardCount;

    public int PeakBufferUsage => peakBufferUsage;
    // ReSharper restore ConvertToAutoProperty

    public int CurrentBufferUsage
    {
        get
        {
            lock (sync)
            {
                return count;
            }
        }
    }

    public int MaxBufferSize => maxBufferSize;

    public SerialLineReader(
        SerialPort serialPort,
        byte[]? delimiter = null,
        int maxBufferSize = 65536,
        bool ownsSerialPort = true)
    {
        this.serialPort = serialPort ?? throw new ArgumentNullException(nameof(serialPort));
        this.delimiter = delimiter ?? [(byte)'\n'];
        if (this.delimiter.Length == 0)
        {
            throw new ArgumentException("Delimiter cannot be empty", nameof(delimiter));
        }
        this.maxBufferSize = maxBufferSize;
        this.ownsSerialPort = ownsSerialPort;
        buffer = ArrayPool<byte>.Shared.Rent(maxBufferSize);
        head = 0;
        tail = 0;
        count = 0;
        searchStart = 0;

        serialPort.DataReceived += OnDataReceived;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref disposed, 1, 0) == 0)
        {
            serialPort.DataReceived -= OnDataReceived;

            ArrayPool<byte>.Shared.Return(buffer);
            buffer = null!;

            if (ownsSerialPort)
            {
                serialPort.Dispose();
            }
        }
    }

    public int DiscardBuffer()
    {
        lock (sync)
        {
            var discardedBytes = count;

            // [MEMO] これを採用するか？
            // 統計情報を更新（空でも呼び出し回数はカウント）
            totalDiscardCount++;

            if (discardedBytes > 0)
            {
                Debug.WriteLine($"[Discard] Discarding {discardedBytes} bytes from buffer");
                totalBytesDiscarded += discardedBytes;
            }
            else
            {
                Debug.WriteLine("[Discard] Buffer is already empty");
            }

            // 位置情報を初期状態にリセット
            head = 0;
            tail = 0;
            count = 0;
            searchStart = 0;

            Debug.WriteLine("[Discard] Buffer reset: head=0, tail=0, count=0, searchStart=0");

            return discardedBytes;
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        // TODO
        lock (sync)
        {
            try
            {
                var bytesToRead = serialPort.BytesToRead;
                if (bytesToRead == 0)
                {
                    return;
                }

                Debug.WriteLine($"[Receive] BytesToRead={bytesToRead}, Before: head={head}, tail={tail}, count={count}, searchStart={searchStart}");

                // 受信データをリングバッファに書き込み
                WriteToRingBuffer(bytesToRead);

                Debug.WriteLine($"[Write] After: head={head}, tail={tail}, count={count}");

                // 終端文字列を探して処理
                ProcessLines();

                Debug.WriteLine($"[Process] After: head={head}, tail={tail}, count={count}, searchStart={searchStart}");
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Debug.WriteLine($"[Error] {ex.Message}");
                Debug.WriteLine($"[Error] StackTrace: {ex.StackTrace}");
            }
        }
    }

    private void WriteToRingBuffer(int bytesToRead)
    {
        var availableSpace = maxBufferSize - count;
        var bytesToWrite = bytesToRead;

        // バッファが満杯の場合、古いデータを破棄
        if (bytesToRead > availableSpace)
        {
            var discardedBytes = bytesToRead - availableSpace;
            Debug.WriteLine($"[Overflow] Discarding={discardedBytes} bytes");

            // 統計情報を更新
            totalOverflowCount++;
            totalBytesDiscarded += discardedBytes;

            // 古いデータを破棄（headを進める）
            var oldHead = head;
            head = (head + discardedBytes) % maxBufferSize;
            count -= discardedBytes;

            // 検索開始位置を調整
            searchStart = Math.Max(0, searchStart - discardedBytes);

            Debug.WriteLine($"[Overflow] head: {oldHead}->{head}, count={count}, searchStart={searchStart}");

            BufferOverflow?.Invoke(this, discardedBytes);
        }

        // データを読み込み
        var totalBytesRead = 0;
        while (totalBytesRead < bytesToWrite)
        {
            // 現在のtail位置から書き込める連続領域のサイズを計算
            int contiguousSpace;
            if (count == 0)
            {
                contiguousSpace = maxBufferSize - tail;
            }
            else if (tail >= head)
            {
                contiguousSpace = maxBufferSize - tail;
            }
            else
            {
                contiguousSpace = head - tail;
            }

            var chunkSize = Math.Min(bytesToWrite - totalBytesRead, contiguousSpace);

            if (chunkSize <= 0)
            {
                Debug.WriteLine($"[Write] Error: chunkSize={chunkSize}, tail={tail}, head={head}, count={count}, contiguousSpace={contiguousSpace}");
                break;
            }

            var bytesRead = serialPort.Read(buffer, tail, chunkSize);

            if (bytesRead == 0)
            {
                break;
            }

            Debug.WriteLine($"[Write] Position={tail}, Length={bytesRead}");

            tail = (tail + bytesRead) % maxBufferSize;
            count += bytesRead;
            totalBytesRead += bytesRead;

            // 統計情報を更新
            totalBytesReceived += bytesRead;

            // ピークバッファ使用量を更新
            if (count > peakBufferUsage)
            {
                peakBufferUsage = count;
            }
        }
    }

    private void ProcessLines()
    {
        var lineCount = 0;

        while (count > 0)
        {
            // 前回の検索位置から終端文字列を検索
            var delimiterIndex = FindDelimiterInRingBuffer();

            if (delimiterIndex == -1)
            {
                // 終端が見つからない場合、次回の検索開始位置を更新
                searchStart = Math.Max(0, count - delimiter.Length + 1);
                Debug.WriteLine($"[Search] Delimiter not found, searchStart updated to {searchStart}");
                break;
            }

            lineCount++;
            Debug.WriteLine($"[Process] Line#{lineCount}, DelimiterAt={delimiterIndex}, LineLength={delimiterIndex}");

            // 終端までのデータを取得してイベント発火
            if (delimiterIndex > 0)
            {
                // 統計情報を更新
                totalLinesReceived++;

                // データをコピーせずに処理できる場合
                if (head + delimiterIndex <= maxBufferSize)
                {
                    // 連続したメモリ領域として処理
                    ReadOnlySpan<byte> line = buffer.AsSpan(head, delimiterIndex);
                    Debug.WriteLine($"[Process] Contiguous read: offset={head}, length={delimiterIndex}");
                    LineReceived?.Invoke(this, line);
                }
                else
                {
                    // リングバッファの境界をまたぐ場合
                    ProcessRingWrapLine(delimiterIndex);
                }
            }
            else
            {
                // 空行
                totalEmptyLinesSkipped++;
                Debug.WriteLine("[Process] Empty line skipped");
            }

            // 処理済みデータと終端文字列を削除
            var bytesToRemove = delimiterIndex + delimiter.Length;
            Debug.WriteLine($"[Process] Removing {bytesToRemove} bytes (line={delimiterIndex}, delimiter={delimiter.Length})");

            head = (head + bytesToRemove) % maxBufferSize;
            count -= bytesToRemove;

            // 検索開始位置をリセット（新しい行の検索は先頭から）
            searchStart = 0;
        }
    }

    private void ProcessRingWrapLine(int lineLength)
    {
        // サイズが小さい場合はstackallocを使用
        if (lineLength <= StackAllocThreshold)
        {
            Span<byte> tempBuffer = stackalloc byte[lineLength];
            CopyFromRingBuffer(tempBuffer);
            Debug.WriteLine($"[Process] Ring-wrap read (stackalloc): length={lineLength}");
            LineReceived?.Invoke(this, tempBuffer);
        }
        else
        {
            // サイズが大きい場合はArrayPoolを使用
            var tempBuffer = ArrayPool<byte>.Shared.Rent(lineLength);
            try
            {
                CopyFromRingBuffer(tempBuffer.AsSpan(0, lineLength));
                ReadOnlySpan<byte> line = tempBuffer.AsSpan(0, lineLength);
                Debug.WriteLine($"[Process] Ring-wrap read (ArrayPool): length={lineLength}");
                LineReceived?.Invoke(this, line);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(tempBuffer);
            }
        }
    }

    private int FindDelimiterInRingBuffer()
    {
        // 検索に必要な最小データ量をチェック
        if (count < delimiter.Length)
        {
            return -1;
        }

        // 検索開始位置から検索
        var searchEnd = count - delimiter.Length + 1;

        Debug.WriteLine($"[Search] Start={searchStart}, End={searchEnd}, Count={count}");

        // 単一バイトの終端文字列の場合は最適化
        if (delimiter.Length == 1)
        {
            var delimiterByte = delimiter[0];
            for (var i = searchStart; i < count; i++)
            {
                var index = (head + i) % maxBufferSize;
                if (buffer[index] == delimiterByte)
                {
                    Debug.WriteLine($"[Search] Found at position {i}");
                    return i;
                }
            }
            return -1;
        }

        // 複数バイトの終端文字列の場合
        for (var i = searchStart; i < searchEnd; i++)
        {
            var found = true;
            for (var j = 0; j < delimiter.Length; j++)
            {
                var index = (head + i + j) % maxBufferSize;
                if (buffer[index] != delimiter[j])
                {
                    found = false;
                    break;
                }
            }
            if (found)
            {
                Debug.WriteLine($"[Search] Found at position {i}");
                return i;
            }
        }

        return -1;
    }

    private void CopyFromRingBuffer(Span<byte> destination)
    {
        var sourceIndex = head;
        var remaining = destination.Length;
        var destIndex = 0;

        while (remaining > 0)
        {
            var contiguousLength = Math.Min(remaining, maxBufferSize - sourceIndex);
            buffer.AsSpan(sourceIndex, contiguousLength).CopyTo(destination.Slice(destIndex, contiguousLength));

            sourceIndex = (sourceIndex + contiguousLength) % maxBufferSize;
            destIndex += contiguousLength;
            remaining -= contiguousLength;
        }
    }

#pragma warning disable CA1024
    public Statistics GetStatistics()
    {
        lock (sync)
        {
            return new Statistics
            {
                TotalLinesReceived = totalLinesReceived,
                TotalBytesReceived = totalBytesReceived,
                TotalOverflowCount = totalOverflowCount,
                TotalBytesDiscarded = totalBytesDiscarded,
                TotalEmptyLinesSkipped = totalEmptyLinesSkipped,
                TotalDiscardCount = totalDiscardCount,
                PeakBufferUsage = peakBufferUsage,
                CurrentBufferUsage = count
            };
        }
    }
#pragma warning restore CA1024

    public sealed class Statistics
    {
        public long TotalLinesReceived { get; init; }

        public long TotalBytesReceived { get; init; }

        public long TotalOverflowCount { get; init; }

        public long TotalBytesDiscarded { get; init; }

        public long TotalEmptyLinesSkipped { get; init; }

        public long TotalDiscardCount { get; init; }

        public int PeakBufferUsage { get; init; }

        public int CurrentBufferUsage { get; init; }

        // TODO
        public override string ToString()
        {
            return $"Lines: {TotalLinesReceived}, " +
                   $"Bytes: {TotalBytesReceived}, " +
                   $"Overflows: {TotalOverflowCount}, " +
                   $"Discarded: {TotalBytesDiscarded}, " +
                   $"ManualDiscards: {TotalDiscardCount}, " +
                   $"EmptyLines: {TotalEmptyLinesSkipped}";
        }
    }
}
