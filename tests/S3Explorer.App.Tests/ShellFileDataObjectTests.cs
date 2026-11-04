using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Xunit;

namespace S3Explorer.App.Tests;

[Collection(MainFormInteractionCollection.Name)]
public sealed class ShellFileDataObjectTests
{
    [Fact]
    public void DescriptorPreservesUnicodeDirectoriesLargeSizesAndUtcWriteTime()
    {
        var modified = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
        var descriptors = ShellFileDataObject.BuildFileGroupDescriptor([
            new("目录", true, 0, null),
            new("目录/large.bin", false, (1L << 33) + 7, modified)]);
        Assert.Equal(4 + 2 * 592, descriptors.Length);
        Assert.Equal(2, BitConverter.ToInt32(descriptors));
        Assert.Equal(0x10u, BitConverter.ToUInt32(descriptors, 4 + 36));
        Assert.Equal(0u, BitConverter.ToUInt32(descriptors, 4) & 0x40u);
        int offset = 4 + 592;
        Assert.Equal(2u, BitConverter.ToUInt32(descriptors, offset + 64));
        Assert.Equal(7u, BitConverter.ToUInt32(descriptors, offset + 68));
        Assert.Equal(modified.UtcDateTime.ToFileTimeUtc(), BitConverter.ToInt64(descriptors, offset + 56));
        Assert.Equal("目录\\large.bin", System.Text.Encoding.Unicode.GetString(descriptors, offset + 72, 520).TrimEnd('\0'));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:\\file")]
    [InlineData("a\\..\\b")]
    [InlineData("a\0b")]
    [InlineData("a//b")]
    public void DescriptorRejectsUnsafePaths(string name) => Assert.Throws<ArgumentException>(() =>
        ShellFileDataObject.BuildFileGroupDescriptor([new(name, false, 1, null)]));

    [Fact]
    public void EmptyFileContentsValidateEvenWhenConsumerOnlyRequestsStat()
    {
        var opened = 0;
        using var data = new ShellFileDataObject([new("empty.txt", false, 0, null)],
            _ => { opened++; throw new UnauthorizedAccessException("remote read denied"); }, "token");
        var descriptor = ShellFileDataObject.BuildFileGroupDescriptor([new("empty.txt", false, 0, null)]);
        Assert.Equal(0u, BitConverter.ToUInt32(descriptor, 4) & 0x40u);
        var format = Format("FileContents", 0, TYMED.TYMED_ISTREAM);
        Assert.Throws<UnauthorizedAccessException>(() => data.GetData(ref format, out _));
        Assert.Equal(1, opened);
    }

    [Fact]
    public void DataValidationChecksAspectIndexMediumAndDirectories()
    {
        using var data = CreateData(_ => throw new Exception("Must remain lazy"));
        var format = Format("FileContents", 0, TYMED.TYMED_ISTREAM);
        Assert.Equal(0, data.QueryGetData(ref format));
        format.lindex = -1;
        Assert.Equal(ShellFileDataObject.IndexError, data.QueryGetData(ref format));
        format.lindex = 1;
        Assert.Equal(ShellFileDataObject.IndexError, data.QueryGetData(ref format));
        format.lindex = 0;
        format.tymed = TYMED.TYMED_HGLOBAL;
        Assert.Equal(ShellFileDataObject.TymedError, data.QueryGetData(ref format));
        format.tymed = TYMED.TYMED_ISTREAM;
        format.dwAspect = DVASPECT.DVASPECT_ICON;
        Assert.Equal(ShellFileDataObject.AspectError, data.QueryGetData(ref format));
        format = Format("FileGroupDescriptorW", 0, TYMED.TYMED_HGLOBAL);
        Assert.Equal(ShellFileDataObject.IndexError, data.QueryGetData(ref format));
    }

    [Fact]
    public void TokenOnlyObjectDoesNotAdvertiseVirtualFiles()
    {
        using var data = new ShellFileDataObject([], _ => throw new Exception(), "token");
        var descriptor = Format("FileGroupDescriptorW", -1, TYMED.TYMED_HGLOBAL);
        Assert.Equal(ShellFileDataObject.FormatError, data.QueryGetData(ref descriptor));
        var contents = Format("FileContents", 0, TYMED.TYMED_ISTREAM);
        Assert.Equal(ShellFileDataObject.FormatError, data.QueryGetData(ref contents));
        var token = Format(ShellFileDataObject.TokenFormatName, -1, TYMED.TYMED_HGLOBAL);
        Assert.Equal(0, data.QueryGetData(ref token));
        var items = new FORMATETC[8];
        int[] count = [0];
        Assert.Equal(1, data.EnumFormatEtc(DATADIR.DATADIR_GET).Next(8, items, count));
        Assert.Equal(2, count[0]);
    }

    [Fact]
    public void HGlobalMediumUsesNativeOwnershipAndAlwaysPrefersCopy()
    {
        using var data = CreateData(_ => throw new Exception());
        var format = Format("Preferred DropEffect", -1, TYMED.TYMED_HGLOBAL);
        data.GetData(ref format, out var medium);
        try
        {
            Assert.Equal(TYMED.TYMED_HGLOBAL, medium.tymed);
            Assert.Null(medium.pUnkForRelease);
            IntPtr pointer = GlobalLock(medium.unionmember);
            try { Assert.Equal(1, Marshal.ReadInt32(pointer)); }
            finally { GlobalUnlock(medium.unionmember); }
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    [Fact]
    public void GetDataAndStatSeekCloneDoNotOpenSourceAndNativeReadReturnsShortRead()
    {
        int opened = 0;
        using var data = CreateData(_ => { opened++; return new MemoryStream([10, 20, 30]); });
        var format = Format("FileContents", 0, TYMED.TYMED_ISTREAM);
        data.GetData(ref format, out var medium);
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try
        {
            Assert.Equal(0, opened);
            Assert.Equal(TYMED.TYMED_ISTREAM, medium.tymed);
            var stream = (IShellReadStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            Assert.Equal(0, stream.Stat(out var stat, 1));
            Assert.Equal(3, stat.cbSize);
            Assert.Null(stat.pwcsName);
            Assert.Equal(0, stream.Seek(1, 0, IntPtr.Zero));
            Assert.Equal(0, stream.Clone(out var clone));
            Assert.Equal(0, opened);
            // Invoke through the CCW's actual native vtable, not a managed call.
            IntPtr vtable = Marshal.ReadIntPtr(medium.unionmember);
            var nativeRead = Marshal.GetDelegateForFunctionPointer<ReadDelegate>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
            Assert.Equal(1, nativeRead(medium.unionmember, buffer, 5, buffer + 8));
            Assert.Equal(2, Marshal.ReadInt32(buffer + 8));
            Assert.Equal(20, Marshal.ReadByte(buffer));
            Assert.Equal(30, Marshal.ReadByte(buffer + 1));
            Assert.Equal(1, opened);
            Assert.Equal(0, clone.Read(buffer, 1, buffer + 8));
            Assert.Equal(20, Marshal.ReadByte(buffer));
            Assert.Equal(2, opened);
            Assert.Equal(1, nativeRead(medium.unionmember, buffer, 1, buffer + 8));
            Assert.Equal(0, Marshal.ReadInt32(buffer + 8));
        }
        finally { Marshal.FreeHGlobal(buffer); ReleaseStgMedium(ref medium); }
    }

    [Fact]
    public void StreamFailureAndCancellationAreNotReportedAsEof()
    {
        using var data = CreateData(_ => throw new IOException("download failed"));
        var stream = new ShellFileReadStream(data, 0, new("file", false, 3, null));
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            Assert.Equal(ShellFileDataObject.ReadFault, stream.Read(buffer, 1, buffer + 4));
            Assert.Equal(0, Marshal.ReadInt32(buffer + 4));
            data.Dispose();
            Assert.Equal(unchecked((int)0x800704C7), stream.Read(buffer, 1, buffer + 4));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Fact]
    public void EmptyFileStillValidatesOnFirstRealReadAndDisposedMetadataFails()
    {
        int opened = 0;
        using var data = CreateData(_ => { opened++; throw new UnauthorizedAccessException(); });
        var stream = new ShellFileReadStream(data, 0, new("empty", false, 0, null));
        IntPtr buffer = Marshal.AllocHGlobal(1);
        try
        {
            Assert.Equal(0, stream.Read(IntPtr.Zero, 0, IntPtr.Zero));
            Assert.Equal(0, opened);
            Assert.Equal(ShellFileDataObject.AccessDenied, stream.Read(buffer, 1, IntPtr.Zero));
            Assert.Equal(1, opened);
            data.Dispose();
            Assert.True(stream.Stat(out _, 1) < 0);
            Assert.True(stream.Clone(out _) < 0);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Fact]
    public void TruncatedSourceIsAnErrorAndReadRequestsStayBounded()
    {
        var source = new BoundedSource();
        using var data = CreateData(_ => source);
        var stream = new ShellFileReadStream(data, 0, new("large", false, 1024 * 1024, null));
        IntPtr buffer = Marshal.AllocHGlobal(128 * 1024);
        try
        {
            Assert.Equal(0, stream.Read(buffer, 128 * 1024, IntPtr.Zero));
            Assert.Equal(64 * 1024, source.LargestRequest);
            source.Truncated = true;
            Assert.Equal(ShellFileDataObject.ReadFault, stream.Read(buffer, 1, IntPtr.Zero));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Fact]
    public void CopyToUsesBoundedBuffersAndAccurate64BitCounts()
    {
        var source = new BoundedSource();
        using var data = CreateData(_ => source);
        var stream = new ShellFileReadStream(data, 0, new("large", false, 200_000, null));
        var sink = new CountingSink();
        IntPtr counts = Marshal.AllocHGlobal(16);
        try
        {
            Assert.Equal(1, stream.CopyTo(sink, 300_000, counts, counts + 8));
            Assert.Equal(200_000L, Marshal.ReadInt64(counts));
            Assert.Equal(200_000L, Marshal.ReadInt64(counts + 8));
            Assert.Equal(200_000UL, sink.TotalWritten);
            Assert.True(sink.LargestWrite <= 64 * 1024);
            Assert.True(source.LargestRequest <= 64 * 1024);
        }
        finally { Marshal.FreeHGlobal(counts); }
    }

    [Fact]
    public void SeekSupportsLargePositionsAndRejectsNegativeAndOverflowWithoutOpening()
    {
        using var data = CreateData(_ => throw new Exception("Seek must not download"));
        var stream = new ShellFileReadStream(data, 0, new("large", false, 1L << 34, null));
        IntPtr position = Marshal.AllocHGlobal(8);
        try
        {
            Assert.Equal(0, stream.Seek(-1, 2, position));
            Assert.Equal((1L << 34) - 1, Marshal.ReadInt64(position));
            Assert.True(stream.Seek(-1, 0, position) < 0);
            Assert.True(stream.Seek(long.MaxValue, 1, position) < 0);
            Assert.Equal(ShellFileDataObject.AccessDenied, stream.SetSize(0));
            Assert.Equal(ShellFileDataObject.AccessDenied, stream.Write(IntPtr.Zero, 0, IntPtr.Zero));
        }
        finally { Marshal.FreeHGlobal(position); }
    }

    [Fact]
    public void AsyncCapabilityExposesNativeBoolAndStableOperationState()
    {
        using var data = CreateData(_ => throw new Exception());
        IntPtr pointer = Marshal.GetComInterfaceForObject(data, typeof(IDataObjectAsyncCapability));
        IntPtr output = Marshal.AllocHGlobal(4);
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(pointer);
            var getAsync = Marshal.GetDelegateForFunctionPointer<BoolOutputDelegate>(Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size));
            Assert.Equal(0, getAsync(pointer, output));
            Assert.Equal(1, Marshal.ReadInt32(output));
            Assert.Equal(0, data.StartOperation(IntPtr.Zero));
            var inOperation = Marshal.GetDelegateForFunctionPointer<BoolOutputDelegate>(Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size));
            Assert.Equal(0, inOperation(pointer, output));
            Assert.Equal(1, Marshal.ReadInt32(output));
            Assert.Equal(0, data.EndOperation(0, IntPtr.Zero, 1));
            Assert.Equal(0, inOperation(pointer, output));
            Assert.Equal(0, Marshal.ReadInt32(output));
        }
        finally { Marshal.FreeHGlobal(output); Marshal.Release(pointer); }
    }

    [Fact]
    public void EnumeratorsCloneTheirPositionAndRejectSetDirection()
    {
        using var data = CreateData(_ => throw new Exception());
        var enumerator = data.EnumFormatEtc(DATADIR.DATADIR_GET);
        Assert.Equal(0, enumerator.Skip(1));
        enumerator.Clone(out var clone);
        var originalItem = new FORMATETC[1];
        var clonedItem = new FORMATETC[1];
        Assert.Equal(0, enumerator.Next(1, originalItem, null));
        Assert.Equal(0, clone.Next(1, clonedItem, null));
        Assert.Equal(originalItem[0].cfFormat, clonedItem[0].cfFormat);
        Assert.Throws<COMException>(() => data.EnumFormatEtc(DATADIR.DATADIR_SET));
    }

    [Fact]
    public void DisposalReleasesSessionExactlyOnce()
    {
        int released = 0;
        var data = new ShellFileDataObject([], _ => throw new Exception(), "token", () => released++);
        data.Dispose();
        data.Dispose();
        Assert.Equal(1, released);
    }

    [Fact]
    public void OleRoundTripReadsOnConsumerThreadAndPreservesClipboardOwnership()
    {
        RunSta(async () =>
        {
            int uiThread = Environment.CurrentManagedThreadId;
            int openedThread = 0;
            using var data = CreateData(_ => { openedThread = Environment.CurrentManagedThreadId; return new MemoryStream([10, 20, 30]); });
            await ShellClipboardPublisher.SetAsync(data, TestContext.Current.CancellationToken);
            try
            {
                Assert.True(ShellClipboardPublisher.IsCurrent(data));
                Assert.Equal(0, openedThread);
                var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var consumer = new Thread(() =>
                {
                    int initialized = OleInitialize(IntPtr.Zero);
                    try
                    {
                        Marshal.ThrowExceptionForHR(initialized);
                        Marshal.ThrowExceptionForHR(OleGetClipboard(out var proxy));
                        try
                        {
                            var format = Format("FileContents", 0, TYMED.TYMED_ISTREAM);
                            proxy.GetData(ref format, out var medium);
                            IntPtr buffer = Marshal.AllocHGlobal(8);
                            try
                            {
                                IntPtr vtable = Marshal.ReadIntPtr(medium.unionmember);
                                var read = Marshal.GetDelegateForFunctionPointer<ReadDelegate>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
                                Assert.Equal(0, read(medium.unionmember, buffer, 3, buffer + 4));
                                Assert.Equal(3, Marshal.ReadInt32(buffer + 4));
                                completed.SetResult(Marshal.ReadByte(buffer));
                            }
                            finally { Marshal.FreeHGlobal(buffer); ReleaseStgMedium(ref medium); }
                        }
                        finally { if (Marshal.IsComObject(proxy)) Marshal.ReleaseComObject(proxy); }
                    }
                    catch (Exception ex) { completed.TrySetException(ex); }
                    finally { if (initialized >= 0) OleUninitialize(); }
                }) { IsBackground = true };
                consumer.SetApartmentState(ApartmentState.STA);
                consumer.Start();
                Assert.Equal(10, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                Assert.NotEqual(uiThread, openedThread);
            }
            finally { await ShellClipboardPublisher.ClearAsync(data); }
        });
    }

    [Fact]
    public void StalePublicationAndClearCannotClobberNewerCopyOrExternalText()
    {
        RunSta(async () =>
        {
            using var first = new ShellFileDataObject([], _ => throw new Exception(), "first");
            using var second = new ShellFileDataObject([], _ => throw new Exception(), "second");
            using var late = new ShellFileDataObject([], _ => throw new Exception(), "late");
            await ShellClipboardPublisher.SetAsync(first, TestContext.Current.CancellationToken);
            try
            {
                await ShellClipboardPublisher.SetAsync(second, TestContext.Current.CancellationToken);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShellClipboardPublisher.SetAsync(late, TestContext.Current.CancellationToken, expected: first));
                await ShellClipboardPublisher.ClearAsync(first);
                Assert.True(ShellClipboardPublisher.IsCurrent(second));
                Clipboard.SetText("external-clipboard-sentinel");
                Assert.False(ShellClipboardPublisher.IsCurrent(second));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShellClipboardPublisher.SetAsync(late, TestContext.Current.CancellationToken, expected: second));
                await ShellClipboardPublisher.ClearAsync(second);
                Assert.Equal("external-clipboard-sentinel", Clipboard.GetText());
                Clipboard.Clear();
            }
            finally { await ShellClipboardPublisher.ClearAsync(first); await ShellClipboardPublisher.ClearAsync(second); }
        });
    }

    private static void RunSta(Func<Task> action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new Control();
                _ = control.Handle;
                var task = action();
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Application.DoEvents();
                    Thread.Sleep(1);
                }
                Assert.True(task.IsCompleted, "OLE clipboard test timed out.");
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "OLE clipboard STA timed out.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static ShellFileDataObject CreateData(Func<int, Stream> factory) => new([
        new("file.bin", false, 3, null), new("empty", true, 0, null)], factory, "immutable-token");
    private static FORMATETC Format(string name, int index, TYMED tymed) => new()
    {
        cfFormat = unchecked((short)RegisterClipboardFormat(name)), lindex = index,
        dwAspect = DVASPECT.DVASPECT_CONTENT, tymed = tymed
    };
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadDelegate(IntPtr self, IntPtr buffer, uint count, IntPtr read);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BoolOutputDelegate(IntPtr self, IntPtr output);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int OleGetClipboard(out System.Runtime.InteropServices.ComTypes.IDataObject data);

    private sealed class CountingSink : IShellReadStream
    {
        public ulong TotalWritten { get; private set; }
        public uint LargestWrite { get; private set; }
        public int Write(IntPtr buffer, uint count, IntPtr bytesWritten)
        {
            TotalWritten += count;
            LargestWrite = Math.Max(LargestWrite, count);
            if (bytesWritten != IntPtr.Zero) Marshal.WriteInt32(bytesWritten, (int)count);
            return 0;
        }
        public int Read(IntPtr buffer, uint count, IntPtr bytesRead) => throw new NotSupportedException();
        public int Seek(long move, uint origin, IntPtr position) => throw new NotSupportedException();
        public int SetSize(ulong size) => throw new NotSupportedException();
        public int CopyTo(IShellReadStream target, ulong count, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public int Commit(uint flags) => throw new NotSupportedException();
        public int Revert() => throw new NotSupportedException();
        public int LockRegion(ulong offset, ulong count, uint type) => throw new NotSupportedException();
        public int UnlockRegion(ulong offset, ulong count, uint type) => throw new NotSupportedException();
        public int Stat(out STATSTG stat, uint flags) => throw new NotSupportedException();
        public int Clone(out IShellReadStream stream) => throw new NotSupportedException();
    }

    private sealed class BoundedSource : Stream
    {
        public int LargestRequest { get; private set; }
        public bool Truncated { get; set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 1024 * 1024;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) { LargestRequest = Math.Max(LargestRequest, count); if (Truncated) return 0; Array.Clear(buffer, offset, count); Position += count; return count; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
