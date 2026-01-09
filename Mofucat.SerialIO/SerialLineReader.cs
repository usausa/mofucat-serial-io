namespace Mofucat.SerialIO;

using System.Buffers;
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

    private readonly Lock sync = new();
    private readonly SerialPort serialPort;
    private readonly byte[] delimiter;
    private readonly int maxBufferSize;
    private readonly bool ownsSerialPort;

    private int disposed;

    private byte[] buffer;
    private int head;  // read position
    private int tail;  // write position
    private int count; // buffered data size
    private int search; // next search start position (relative to head)

    // ------------------------------------------------------------
    // Statics
    // ------------------------------------------------------------

    private long totalLinesReceived;
    private long totalBytesReceived;
    private long totalOverflowCount;
    private long totalBytesDiscarded;
    private long totalEmptyLinesSkipped;
    private long totalDiscardCount;
    private int peakBufferUsage;

    public long TotalLinesReceived
    {
        get
        {
            lock (sync)
            {
                return totalLinesReceived;
            }
        }
    }

    public long TotalBytesReceived
    {
        get
        {
            lock (sync)
            {
                return totalBytesReceived;
            }
        }
    }

    public long TotalOverflowCount
    {
        get
        {
            lock (sync)
            {
                return totalOverflowCount;
            }
        }
    }

    public long TotalBytesDiscarded
    {
        get
        {
            lock (sync)
            {
                return totalBytesDiscarded;
            }
        }
    }

    public long TotalEmptyLinesSkipped
    {
        get
        {
            lock (sync)
            {
                return totalEmptyLinesSkipped;
            }
        }
    }

    public long TotalDiscardCount
    {
        get
        {
            lock (sync)
            {
                return totalDiscardCount;
            }
        }
    }

    public int PeakBufferUsage
    {
        get
        {
            lock (sync)
            {
                return peakBufferUsage;
            }
        }
    }

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

    // ------------------------------------------------------------
    // Constructor
    // ------------------------------------------------------------

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
        search = 0;

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

    // ------------------------------------------------------------
    // Discard
    // ------------------------------------------------------------

    public int DiscardBuffer()
    {
        lock (sync)
        {
            var discardedBytes = count;

            // Update statistics
            totalDiscardCount++;
            if (discardedBytes > 0)
            {
                totalBytesDiscarded += discardedBytes;
            }

            // Reset pointers
            head = 0;
            tail = 0;
            count = 0;
            search = 0;

            return discardedBytes;
        }
    }

    // ------------------------------------------------------------
    // Receive Handling
    // ------------------------------------------------------------

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        lock (sync)
        {
            var bytesToRead = serialPort.BytesToRead;
            if (bytesToRead == 0)
            {
                return;
            }

            // Write data to ring buffer
            WriteToRingBuffer(bytesToRead);

            // Parse lines
            ProcessLines();

            System.Diagnostics.Debug.WriteLine($"Data received after. head=[{head}], tail=[{tail}], count=[{count}], search=[{search}]");
        }
    }

    private void WriteToRingBuffer(int bytesToRead)
    {
        var availableSpace = maxBufferSize - count;
        var bytesToWrite = bytesToRead;

        // Discard old data if overflow
        if (bytesToRead > availableSpace)
        {
            var discardedBytes = bytesToRead - availableSpace;

            // Update statics
            totalOverflowCount++;
            totalBytesDiscarded += discardedBytes;

            // Discard old data (move head forward)
            head = (head + discardedBytes) % maxBufferSize;
            count -= discardedBytes;

            // Set search position
            search = Math.Max(0, search - discardedBytes);

#pragma warning disable CA1031
            try
            {
                BufferOverflow?.Invoke(this, discardedBytes);
            }
            catch
            {
                // Ignore
            }
#pragma warning restore CA1031
        }

        // Read data from SerialPort into ring buffer
        var totalBytesRead = 0;
        while (totalBytesRead < bytesToWrite)
        {
            // Calculate contiguous space available at tail
            int contiguousSpace;
            if ((count == 0) || (tail >= head))
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
                break;
            }

            // Read from SerialPort
            var bytesRead = serialPort.Read(buffer, tail, chunkSize);
            if (bytesRead == 0)
            {
                break;
            }

            // Update tail and count
            tail = (tail + bytesRead) % maxBufferSize;
            count += bytesRead;
            totalBytesRead += bytesRead;

            // Update statistics
            totalBytesReceived += bytesRead;
            if (count > peakBufferUsage)
            {
                peakBufferUsage = count;
            }
        }
    }

    private void ProcessLines()
    {
        while (count > 0)
        {
            // Find delimiter in ring buffer
            var delimiterIndex = FindDelimiterInRingBuffer();
            if (delimiterIndex == -1)
            {
                // Update search position for next time if not found
                search = Math.Max(0, count - delimiter.Length + 1);
                break;
            }

            // Fire LineReceived event
            if (delimiterIndex > 0)
            {
                // Update statistics
                totalLinesReceived++;

                // Check if line is contiguous
                if (head + delimiterIndex <= maxBufferSize)
                {
                    // Process contiguous line
                    LineReceived?.Invoke(this, buffer.AsSpan(head, delimiterIndex));
                }
                else
                {
                    // Process ring-wrapped line
                    ProcessRingWrapLine(delimiterIndex);
                }
            }
            else
            {
                // Empty line skipped
                totalEmptyLinesSkipped++;
            }

            // Move head past the delimiter
            var bytesToRemove = delimiterIndex + delimiter.Length;
            head = (head + bytesToRemove) % maxBufferSize;
            count -= bytesToRemove;

            // Reset search position
            search = 0;
        }
    }

    private void ProcessRingWrapLine(int delimiterIndex)
    {
        // If the size is small, use stack allocation
        if (delimiterIndex <= StackAllocThreshold)
        {
            Span<byte> tempBuffer = stackalloc byte[delimiterIndex];
            CopyFromRingBuffer(tempBuffer);
            LineReceived?.Invoke(this, tempBuffer);
        }
        else
        {
            // Use ArrayPool for larger sizes
            var tempBuffer = ArrayPool<byte>.Shared.Rent(delimiterIndex);
            try
            {
                CopyFromRingBuffer(tempBuffer.AsSpan(0, delimiterIndex));
                ReadOnlySpan<byte> line = tempBuffer.AsSpan(0, delimiterIndex);
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
        // Check if enough data to contain delimiter
        if (count < delimiter.Length)
        {
            return -1;
        }

        // Search for delimiter in ring buffer
        var searchEnd = count - delimiter.Length + 1;

        // Single byte delimiter optimization
        if (delimiter.Length == 1)
        {
            var delimiterByte = delimiter[0];
            for (var i = search; i < count; i++)
            {
                var index = (head + i) % maxBufferSize;
                if (buffer[index] == delimiterByte)
                {
                    return i;
                }
            }
            return -1;
        }

        // Multi-byte delimiter search
        for (var i = search; i < searchEnd; i++)
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
                return i;
            }
        }

        return -1;
    }

    private void CopyFromRingBuffer(Span<byte> destination)
    {
        // Copy data from ring buffer to destination span
        var sourceIndex = head;
        var destinationIndex = 0;

        var remaining = destination.Length;
        while (remaining > 0)
        {
            var contiguousLength = Math.Min(remaining, maxBufferSize - sourceIndex);
            buffer.AsSpan(sourceIndex, contiguousLength).CopyTo(destination.Slice(destinationIndex, contiguousLength));

            sourceIndex = (sourceIndex + contiguousLength) % maxBufferSize;
            destinationIndex += contiguousLength;
            remaining -= contiguousLength;
        }
    }

    // ------------------------------------------------------------
    // Statics
    // ------------------------------------------------------------

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
    }
}
